using System;
using System.Collections.Generic;
using UnityEngine;
using WzUnity;

namespace WzComparerR2.Unity
{
    [DefaultExecutionOrder(100)]
    public sealed class WzMapLayerBehaviour : MonoBehaviour
    {
        public WzMapLayer source;
        public float pixelsPerUnit = 100;
        public WzSpriteAnimator prototype;
        public Camera viewCamera;
        public Vector2 repeatSizePixels;
        public Rect repeatBounds;
        public bool isBackground;
        private double elapsedMs;
        [SerializeField] private List<WzSpriteAnimator> copies = new List<WzSpriteAnimator>();

        private void LateUpdate()
        {
            elapsedMs += Time.deltaTime * 1000d;
            Refresh(elapsedMs);
        }

        public void Refresh(double timeMs)
        {
            if (source == null || prototype == null) return;
            var motion = source.motion;
            float x = source.x + Evaluate(motion == null ? null : motion.x, timeMs, 0);
            float y = source.y + Evaluate(motion == null ? null : motion.y, timeMs, 0);
            var background = isBackground ? source.background : null;
            var camera = viewCamera != null ? viewCamera : Camera.main;
            if (background != null && camera != null)
            {
                float cameraX = camera.transform.position.x * pixelsPerUnit;
                float cameraY = -camera.transform.position.y * pixelsPerUnit;
                if (background.parallax)
                {
                    if (!background.scrollX) x += cameraX * (1 + background.rx / 100f);
                    if (!background.scrollY) y += cameraY * (1 + background.ry / 100f);
                }
                if (background.scrollX) x += ScrollOffset(background.rx, background.scrollDistanceX, timeMs, background.repeatX, RepeatDistance(background.cx, repeatSizePixels.x));
                if (background.scrollY) y += ScrollOffset(background.ry, background.scrollDistanceY, timeMs, background.repeatY, RepeatDistance(background.cy, repeatSizePixels.y));
                x = Mathf.Floor(x);
                y = Mathf.Floor(y);
            }
            transform.localPosition = new Vector3(x / pixelsPerUnit, -y / pixelsPerUnit, 0);
            float scale = Evaluate(motion == null ? null : motion.scaleX, timeMs, 1);
            transform.localScale = new Vector3(scale, 1, 1);
            float alpha = source.alpha * Evaluate(motion == null ? null : motion.opacity, timeMs, 1);
            prototype.Opacity = alpha;
            if (background != null) prototype.Sample(string.IsNullOrEmpty(source.action) ? prototype.animationSet.defaultAction : source.action, timeMs);
            else prototype.RefreshPose();
            if (background == null || (!background.repeatX && !background.repeatY) || camera == null) return;
            float tileX = RepeatDistance(background.cx, repeatSizePixels.x) / pixelsPerUnit;
            float tileY = RepeatDistance(background.cy, repeatSizePixels.y) / pixelsPerUnit;
            float halfY = camera.orthographicSize;
            float halfX = halfY * camera.aspect;
            var cameraLocal = transform.InverseTransformPoint(camera.transform.position);
            float minBoundX = source.flip ? -repeatBounds.xMax : repeatBounds.xMin;
            float maxBoundX = source.flip ? -repeatBounds.xMin : repeatBounds.xMax;
            int minX = background.repeatX ? Mathf.FloorToInt((cameraLocal.x - halfX - maxBoundX) / tileX) - 1 : 0;
            int maxX = background.repeatX ? Mathf.CeilToInt((cameraLocal.x + halfX - minBoundX) / tileX) + 1 : 0;
            int minY = background.repeatY ? Mathf.FloorToInt((cameraLocal.y - halfY - repeatBounds.yMax) / tileY) - 1 : 0;
            int maxY = background.repeatY ? Mathf.CeilToInt((cameraLocal.y + halfY - repeatBounds.yMin) / tileY) + 1 : 0;
            int used = 0;
            bool prototypeVisible = false;
            for (int yi = minY; yi <= maxY; yi++)
            for (int xi = minX; xi <= maxX; xi++)
            {
                if (xi == 0 && yi == 0) { prototypeVisible = true; continue; }
                if (used >= copies.Count)
                {
                    var copy = Instantiate(prototype, transform);
                    copy.name = "Repeat " + copies.Count;
                    copies.Add(copy);
                }
                var animator = copies[used++];
                animator.gameObject.SetActive(true);
                animator.transform.localPosition = new Vector3(xi * tileX, yi * tileY, 0);
                animator.Opacity = alpha;
                // All copies sample the same time; entering the viewport never restarts the background.
                animator.Sample(prototype.CurrentAction ?? prototype.animationSet.defaultAction, timeMs);
            }
            prototype.gameObject.SetActive(prototypeVisible);
            if (prototypeVisible) prototype.Sample(string.IsNullOrEmpty(source.action) ? prototype.animationSet.defaultAction : source.action, timeMs);
            for (int index = used; index < copies.Count; index++) copies[index].gameObject.SetActive(false);
        }

        private static float RepeatDistance(float spacing, float fallback) => Mathf.Max(1, Mathf.Abs(spacing) > 0 ? Mathf.Abs(spacing) : Mathf.Abs(fallback));

        private static float ScrollOffset(float rate, float speedDistance, double timeMs, bool repeat, float tileDistance)
        {
            // W distance controls velocity. Only a whole repeated tile is visually equivalent after wrapping.
            double offset = (double)rate * Math.Max(1, speedDistance) * timeMs / 20000d;
            return (float)(repeat ? offset % tileDistance : offset);
        }

        public static float Evaluate(WzMotionChannel channel, double timeMs, float fallback)
        {
            if (channel == null || string.IsNullOrEmpty(channel.kind)) return fallback;
            double time = Math.Max(0, timeMs + channel.phaseMs);
            if (channel.cycleMs > 0) time = channel.loop ? time % channel.cycleMs : Math.Min(time, channel.cycleMs);
            float value = channel.offset;
            switch (channel.kind)
            {
                case "sine": value += channel.amplitude * (float)Math.Sin(channel.cycleMs <= 0 ? 0 : time * Math.PI * 2 / channel.cycleMs); break;
                case "cosine": value += channel.amplitude * (float)Math.Cos(channel.cycleMs <= 0 ? 0 : time * Math.PI * 2 / channel.cycleMs); break;
                case "keyframes":
                    if (channel.keys == null || channel.keys.Count == 0) break;
                    value = channel.keys[channel.keys.Count - 1].value;
                    if (time <= channel.keys[0].timeMs) value = channel.keys[0].value;
                    else for (int index = 0; index < channel.keys.Count - 1; index++)
                    {
                        var first = channel.keys[index]; var second = channel.keys[index + 1];
                        if (time >= second.timeMs) continue;
                        value = first.interpolation == "hold" ? first.value : Mathf.Lerp(first.value, second.value, (float)((time - first.timeMs) / Math.Max(0.001, second.timeMs - first.timeMs)));
                        break;
                    }
                    break;
            }
            return channel.pixelSnap ? Mathf.Round(value) : value;
        }
    }
}
