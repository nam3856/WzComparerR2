using System;
using UnityEngine;

namespace WzComparerR2.Unity
{
    [CreateAssetMenu(menuName = "WZ/Animation Set")]
    public sealed class WzAnimationSet : ScriptableObject
    {
        public string sourceId;
        public string sourcePath;
        public string defaultAction;
        public float pixelsPerUnit = 100;
        public WzUnity.WzMetadata[] metadata = Array.Empty<WzUnity.WzMetadata>();
        public WzAnimationAction[] actions = Array.Empty<WzAnimationAction>();

        public WzAnimationAction Find(string action)
        {
            foreach (var candidate in actions)
                if (string.Equals(candidate.name, action, StringComparison.Ordinal)) return candidate;
            return null;
        }
    }

    [Serializable]
    public sealed class WzAnimationAction
    {
        public string name;
        public bool loop = true;
        public double durationMs;
        public WzAnimationTrack[] tracks = Array.Empty<WzAnimationTrack>();
    }

    [Serializable]
    public sealed class WzAnimationTrack
    {
        public string name;
        public bool loop = true;
        public double startMs;
        public string poseTrack;
        public WzAnimationPose[] poses = Array.Empty<WzAnimationPose>();
        public WzUnity.WzMetadata[] metadata = Array.Empty<WzUnity.WzMetadata>();
        public WzAnimationFrame[] frames = Array.Empty<WzAnimationFrame>();
        public double DurationMilliseconds
        {
            get
            {
                double total = 0;
                foreach (var frame in frames) total += Math.Max(1, frame.durationMs);
                return total;
            }
        }
    }

    [Serializable]
    public sealed class WzAnimationFrame
    {
        public double durationMs = 100;
        public WzRenderSlice[] slices = Array.Empty<WzRenderSlice>();
    }

    [Serializable]
    public sealed class WzRenderSlice
    {
        public string slot;
        public string part;
        public Sprite sprite;
        public Vector2 position;
        public int sortingOrder;
        public float alpha = 1;
        public float endAlpha = 1;
        public bool flipX;
        public Vector2 scale = Vector2.one;
        public float rotation;
        public bool visible = true;
        public int sourceZ;
        public Vector2 origin;
        public Vector2 anchor;
    }

    [Serializable]
    public sealed class WzAnimationPose
    {
        public int poseFrame;
        public int frameIndex = -1;
        public Vector2 anchor;
        public int sourceZ;
        public int sortingOrder;
        public bool visible = true;
        public bool overrideSprite;
        public Sprite sprite;
        public Vector2 origin;
    }
}
