using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace WzComparerR2.Unity
{
    /// <summary>Uses source millisecond durations, including independently timed facial/effect tracks.</summary>
    [DisallowMultipleComponent]
    public sealed class WzSpriteAnimator : MonoBehaviour
    {
        public WzAnimationSet animationSet;
        public Material spriteMaterial;
        public bool playOnEnable = true;
        public string startingAction;
        [SerializeField] private float speed = 1;
        [SerializeField] private bool flipX;
        [SerializeField] private bool useActionLoop = true;
        [SerializeField] private bool loop = true;
        public int sortingOrderOffset;
        public int sortingOrderStride = 1;
        [SerializeField] private float opacity = 1;
        public float Opacity { get => opacity; set => opacity = Mathf.Clamp01(value); }
        public UnityEvent completed = new UnityEvent();
        public event Action<string> Completed;
        public string CurrentAction { get; private set; }
        public bool IsPlaying { get; private set; }
        /// <summary>Optional final sprite substitution; does not change any animation clocks or anchors.</summary>
        [field: NonSerialized]
        public Func<Sprite, Sprite> SpriteResolver { get; set; }
        public float Speed { get => speed; set => speed = Mathf.Max(0, value); }
        public bool FlipX { get => flipX; set { flipX = value; ApplyFrames(); } }
        /// <summary>Setting this overrides the authored loop policy until UseAuthoredLoop is called.</summary>
        public bool Loop { get => useActionLoop ? currentAction != null && currentAction.loop : loop; set { loop = value; useActionLoop = false; } }

        private sealed class TrackState
        {
            public WzAnimationTrack track;
            public double elapsed;
            public bool finished;
            public int frameIndex;
            public float fraction;
            public readonly Dictionary<string, SpriteRenderer> renderers = new Dictionary<string, SpriteRenderer>();
        }

        private WzAnimationAction currentAction;
        private readonly List<TrackState> states = new List<TrackState>();
        private readonly Dictionary<string, Transform> slots = new Dictionary<string, Transform>();
        private readonly Dictionary<string, SpriteRenderer> allRenderers = new Dictionary<string, SpriteRenderer>();
        private readonly Dictionary<SpriteRenderer, Vector2Int> sourceOrders = new Dictionary<SpriteRenderer, Vector2Int>();
        public IEnumerable<KeyValuePair<SpriteRenderer, Vector2Int>> SourceOrders => sourceOrders;
        private double actionElapsed;
        private readonly List<KeyValuePair<SpriteRenderer, Vector2Int>> ordered = new List<KeyValuePair<SpriteRenderer, Vector2Int>>();

        private void OnEnable()
        {
            if (playOnEnable && animationSet != null)
                Play(string.IsNullOrEmpty(CurrentAction) ? (string.IsNullOrEmpty(startingAction) ? animationSet.defaultAction : startingAction) : CurrentAction);
        }

        private void Update() => Advance(Time.deltaTime);

        public void UseAuthoredLoop() => useActionLoop = true;

        public bool Play(string action)
        {
            if (animationSet == null) return false;
            var next = animationSet.Find(action);
            if (next == null) return false;
            if (allRenderers.Count == 0)
            {
                foreach (var fragment in GetComponentsInChildren<WzRenderFragment>(true))
                {
                    if (fragment.GetComponentInParent<WzSpriteAnimator>() != this || string.IsNullOrEmpty(fragment.key)) continue;
                    var renderer = fragment.GetComponent<SpriteRenderer>();
                    if (renderer != null) allRenderers[fragment.key] = renderer;
                }
            }
            currentAction = next;
            CurrentAction = next.name;
            states.Clear();
            sourceOrders.Clear();
            actionElapsed = 0;
            foreach (var renderer in allRenderers.Values) if (renderer != null) renderer.enabled = false;
            foreach (var track in next.tracks)
            {
                if (track.frames.Length == 0) continue;
                var state = new TrackState { track = track, elapsed = -track.startMs };
                states.Add(state);
                CreateRenderers(state);
            }
            IsPlaying = states.Count > 0;
            ApplyFrames();
            return IsPlaying;
        }

        /// <summary>Replace one track without resetting the body or other effect clocks.</summary>
        public bool PlayTrack(string trackName, string action)
        {
            var source = animationSet == null ? null : animationSet.Find(action);
            if (source == null) return false;
            foreach (var track in source.tracks)
            {
                if (track.name != trackName || track.frames.Length == 0) continue;
                var state = states.Find(candidate => candidate.track.name == trackName);
                if (state == null) { state = new TrackState(); states.Add(state); }
                foreach (var renderer in state.renderers.Values) renderer.enabled = false;
                state.renderers.Clear();
                state.track = track;
                state.elapsed = -track.startMs;
                state.frameIndex = 0;
                state.fraction = 0;
                state.finished = false;
                CreateRenderers(state);
                IsPlaying = true;
                ApplyFrames();
                return true;
            }
            return false;
        }

        public void Stop() => IsPlaying = false;
        public void RefreshPose() => ApplyFrames();

        public bool Sample(string action, double milliseconds)
        {
            if (CurrentAction != action || currentAction == null)
                if (!Play(action)) return false;
            foreach (var state in states)
            {
                double time = Math.Max(0, milliseconds - state.track.startMs);
                double duration = state.track.DurationMilliseconds;
                if (duration <= 0) continue;
                time = Loop && state.track.loop ? time % duration : Math.Min(time, duration);
                state.elapsed = milliseconds < state.track.startMs ? -1 : time;
                for (int index = 0; index < state.track.frames.Length; index++)
                {
                    state.frameIndex = index;
                    double delay = Math.Max(1, state.track.frames[index].durationMs);
                    if (time < delay || index == state.track.frames.Length - 1)
                    { state.fraction = (float)Math.Min(1, time / delay); break; }
                    time -= delay;
                }
            }
            ApplyFrames();
            return true;
        }

        /// <summary>Advances deterministically; public for gameplay clocks, previews and validation.</summary>
        public void Advance(float seconds)
        {
            if (!IsPlaying || seconds <= 0 || speed <= 0) return;
            bool primaryFinished = true;
            bool anyPrimary = false;
            actionElapsed += seconds * 1000d * speed;
            foreach (var state in states)
            {
                var duration = state.track.DurationMilliseconds;
                if (duration <= 0) continue;
                var shouldLoop = Loop && state.track.loop;
                state.elapsed += seconds * 1000d * speed;
                if (shouldLoop && state.elapsed >= 0) state.elapsed %= duration;
                else if (state.elapsed >= duration) { state.elapsed = duration; state.finished = true; }
                double remaining = Math.Max(0, state.elapsed);
                state.frameIndex = 0;
                for (int i = 0; i < state.track.frames.Length; i++)
                {
                    var frameDuration = Math.Max(1, state.track.frames[i].durationMs);
                    state.frameIndex = i;
                    if (remaining < frameDuration || i == state.track.frames.Length - 1)
                    {
                        state.fraction = (float)Math.Min(1d, remaining / frameDuration);
                        break;
                    }
                    remaining -= frameDuration;
                }
                // The first track is the action clock. Independent looping effects never prevent completion.
                if (!anyPrimary) { primaryFinished = state.finished; anyPrimary = true; }
            }
            ApplyFrames();
            if (!Loop && anyPrimary && (currentAction.durationMs > 0 ? actionElapsed >= currentAction.durationMs : primaryFinished))
            {
                IsPlaying = false;
                completed.Invoke();
                Completed?.Invoke(CurrentAction);
            }
        }

        private void CreateRenderers(TrackState state)
        {
            foreach (var frame in state.track.frames)
            foreach (var slice in frame.slices)
            {
                var key = SliceKey(state.track.name, slice);
                if (state.renderers.ContainsKey(key)) continue;
                if (!allRenderers.TryGetValue(key, out var renderer) || renderer == null)
                {
                    var slotName = string.IsNullOrEmpty(slice.slot) ? "body" : slice.slot;
                    if (!slots.TryGetValue(slotName, out var slot) || slot == null)
                    {
                        slot = transform.Find(HierarchyName(slotName));
                        if (slot == null) { slot = new GameObject(HierarchyName(slotName)).transform; slot.SetParent(transform, false); }
                        slots[slotName] = slot;
                    }
                    string fragmentName = HierarchyName(state.track.name + ":" + slice.part);
                    var fragment = slot.Find(fragmentName);
                    if (fragment == null) { fragment = new GameObject(fragmentName).transform; fragment.SetParent(slot, false); }
                    renderer = fragment.GetComponent<SpriteRenderer>();
                    if (renderer == null) renderer = fragment.gameObject.AddComponent<SpriteRenderer>();
                    var marker = fragment.GetComponent<WzRenderFragment>();
                    if (marker == null) marker = fragment.gameObject.AddComponent<WzRenderFragment>();
                    marker.key = key;
                    if (spriteMaterial != null) renderer.sharedMaterial = spriteMaterial;
                    allRenderers[key] = renderer;
                }
                state.renderers[key] = renderer;
            }
        }

        private static string SliceKey(string track, WzRenderSlice slice) => track + "\n" + slice.slot + "\n" + slice.part;
        private static string HierarchyName(string name) => (name ?? "body").Replace("%", "%25").Replace("/", "%2F").Replace("\\", "%5C");

        private void ApplyFrames()
        {
            foreach (var state in states)
            {
                foreach (var renderer in state.renderers.Values) if (renderer != null) renderer.enabled = false;
                if (state.track.frames.Length == 0) continue;
                if (state.elapsed < 0) continue;
                var frame = state.track.frames[Mathf.Clamp(state.frameIndex, 0, state.track.frames.Length - 1)];
                WzAnimationPose pose = null;
                if (!string.IsNullOrEmpty(state.track.poseTrack))
                {
                    var poseClock = states.Find(candidate => candidate.track.name == state.track.poseTrack);
                    if (poseClock != null)
                        foreach (var candidate in state.track.poses)
                            if (candidate.poseFrame == poseClock.frameIndex && (candidate.frameIndex < 0 || candidate.frameIndex == state.frameIndex))
                            { pose = candidate; if (candidate.frameIndex >= 0) break; }
                }
                foreach (var slice in frame.slices)
                {
                    if (!state.renderers.TryGetValue(SliceKey(state.track.name, slice), out var renderer) || renderer == null) continue;
                    var sprite = pose != null && pose.overrideSprite ? pose.sprite : slice.sprite;
                    if (SpriteResolver != null) sprite = SpriteResolver(sprite);
                    renderer.enabled = sprite != null && (pose == null ? slice.visible : pose.visible);
                    renderer.sprite = sprite;
                    var origin = pose != null && pose.overrideSprite ? pose.origin : slice.origin;
                    var extent = sprite == null ? Vector2.zero : sprite.rect.size / (2 * animationSet.pixelsPerUnit);
                    var centreOffset = Vector2.Scale(new Vector2(extent.x - origin.x, -extent.y - origin.y), slice.scale);
                    var position = (pose == null ? slice.anchor : pose.anchor) + (Vector2)(Quaternion.Euler(0, 0, slice.rotation) * centreOffset);
                    // Slice position denotes sprite centre. Reflecting it around the root keeps the WZ anchor fixed.
                    renderer.transform.localPosition = new Vector3(flipX ? -position.x : position.x, position.y, 0);
                    renderer.transform.localScale = new Vector3(slice.scale.x, slice.scale.y, 1);
                    renderer.transform.localRotation = Quaternion.Euler(0, 0, flipX ? -slice.rotation : slice.rotation);
                    renderer.flipX = slice.flipX ^ flipX;
                    var order = pose == null ? slice.sortingOrder : pose.sortingOrder;
                    sourceOrders[renderer] = new Vector2Int(pose == null ? slice.sourceZ : pose.sourceZ, order);
                    renderer.sortingOrder = Mathf.Clamp(sortingOrderOffset + order * sortingOrderStride, -32768, 32767);
                    renderer.color = new Color(1, 1, 1, Mathf.Lerp(slice.alpha, slice.endAlpha, state.fraction) * Opacity);
                }
            }
            ordered.Clear();
            foreach (var pair in sourceOrders) if (pair.Key != null && pair.Key.enabled) ordered.Add(pair);
            ordered.Sort((a, b) => { int z = a.Value.x.CompareTo(b.Value.x); return z != 0 ? z : a.Value.y.CompareTo(b.Value.y); });
            for (int index = 0; index < ordered.Count; index++)
                ordered[index].Key.sortingOrder = Mathf.Clamp(sortingOrderOffset + index * sortingOrderStride, -32768, 32767);
        }
    }
}
