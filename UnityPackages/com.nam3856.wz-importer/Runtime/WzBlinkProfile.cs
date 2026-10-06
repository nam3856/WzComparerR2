using System;
using System.Collections.Generic;
using UnityEngine;

namespace WzComparerR2.Unity
{
    public enum WzBlinkExpression { Open, HalfClosed, Closed }

    [Serializable]
    public sealed class WzBlinkSpriteMapping
    {
        public Sprite source;
        public Sprite open;
        public Sprite halfClosed;
        public Sprite closed;
    }

    /// <summary>Eye variants retain the source canvas, pivot and scale so body placement stays unchanged.</summary>
    [CreateAssetMenu(menuName = "WZ/Blink Profile")]
    public sealed class WzBlinkProfile : ScriptableObject
    {
        public WzBlinkSpriteMapping[] mappings = Array.Empty<WzBlinkSpriteMapping>();

        private Dictionary<Sprite, WzBlinkSpriteMapping> lookup;
        private WzBlinkSpriteMapping[] cachedMappings;

        public Sprite Resolve(Sprite source, WzBlinkExpression expression)
        {
            if (source == null) return null;
            if (lookup == null || !ReferenceEquals(cachedMappings, mappings)) RebuildLookup();
            if (!lookup.TryGetValue(source, out var mapping)) return source;
            switch (expression)
            {
                case WzBlinkExpression.HalfClosed: return mapping.halfClosed != null ? mapping.halfClosed : mapping.open;
                case WzBlinkExpression.Closed: return mapping.closed != null ? mapping.closed : mapping.open;
                default: return mapping.open;
            }
        }

        public void RebuildLookup()
        {
            lookup = new Dictionary<Sprite, WzBlinkSpriteMapping>();
            cachedMappings = mappings;
            if (mappings == null) return;
            foreach (var mapping in mappings)
            {
                if (mapping == null || mapping.source == null || mapping.open == null) continue;
                if (!SameGeometry(mapping.source, mapping.open) ||
                    (mapping.halfClosed != null && !SameGeometry(mapping.source, mapping.halfClosed)) ||
                    (mapping.closed != null && !SameGeometry(mapping.source, mapping.closed))) continue;
                lookup[mapping.source] = mapping;
            }
        }

        private static bool SameGeometry(Sprite source, Sprite variant) =>
            (source.rect.size - variant.rect.size).sqrMagnitude < .0001f &&
            (source.pivot - variant.pivot).sqrMagnitude < .0001f &&
            Mathf.Abs(source.pixelsPerUnit - variant.pixelsPerUnit) < .0001f;

        private void OnValidate() => lookup = null;
    }
}
