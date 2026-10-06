using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WzComparerR2.Unity
{
    /// <summary>Compacts source ordering into Unity's signed 16-bit sorting range every frame.</summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(1000)]
    public sealed class WzMapSorting : MonoBehaviour
    {
        private struct Entry
        {
            public SpriteRenderer renderer;
            public SortingGroup group;
            public int container, z, order, fragment, stable;
        }
        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<WzSpriteAnimator> animators = new List<WzSpriteAnimator>();
        private readonly List<SpriteRenderer> previewRenderers = new List<SpriteRenderer>();

        private void LateUpdate() => Refresh();
        public void Refresh()
        {
            GetComponentsInChildren(false, animators);
            entries.Clear();
            int stable = 0;
            foreach (var animator in animators)
            {
                // Registered characters are one group, even when parented beneath the map.
                if (animator.GetComponent<WzMapCharacterSorting>() != null) continue;
                var order = animator.GetComponentInParent<WzMapRenderOrder>();
                if (order == null) continue;
                bool hasSourceOrders = false;
                foreach (var pair in animator.SourceOrders)
                {
                    hasSourceOrders = true;
                    if (pair.Key == null || !pair.Key.enabled || !pair.Key.gameObject.activeInHierarchy) continue;
                    entries.Add(new Entry { renderer = pair.Key, container = order.containerOrder, z = order.baseZ + pair.Value.x, order = order.sourceOrder, fragment = pair.Value.y, stable = stable++ });
                }
                // After an Editor reload, the serialized pose exists but animation clocks have
                // not started. Preserve its imported order without starting the animation.
                if (!hasSourceOrders && !Application.isPlaying)
                {
                    animator.GetComponentsInChildren(false, previewRenderers);
                    foreach (var renderer in previewRenderers)
                    {
                        if (!renderer.enabled || renderer.GetComponentInParent<WzSpriteAnimator>() != animator) continue;
                        entries.Add(new Entry { renderer = renderer, container = order.containerOrder, z = order.baseZ, order = renderer.sortingOrder, stable = stable++ });
                    }
                }
            }
            foreach (var character in WzMapCharacterSorting.Active)
            {
                if (character == null || !character.isActiveAndEnabled || !character.BelongsTo(this)) continue;
                entries.Add(new Entry { group = character.Group, container = character.ResolveContainerOrder(this), z = int.MaxValue, stable = stable++ });
            }
            entries.Sort((a, b) =>
            {
                int result = a.container.CompareTo(b.container);
                if (result == 0) result = a.z.CompareTo(b.z);
                if (result == 0) result = a.order.CompareTo(b.order);
                if (result == 0) result = a.fragment.CompareTo(b.fragment);
                return result == 0 ? a.stable.CompareTo(b.stable) : result;
            });
            int offset = entries.Count > 32767 ? -32768 : 0;
            for (int index = 0; index < entries.Count; index++)
            {
                int value = Mathf.Clamp(index + offset, -32768, 32767);
                if (entries[index].group != null)
                {
                    var group = entries[index].group;
                    group.sortingLayerID = 0;
                    group.sortingOrder = value;
                }
                else if (entries[index].renderer != null) entries[index].renderer.sortingOrder = value;
            }
        }
    }
}
