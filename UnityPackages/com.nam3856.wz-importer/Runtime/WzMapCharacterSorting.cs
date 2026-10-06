using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace WzComparerR2.Unity
{
    /// <summary>Places a character between map layers using the foothold below its feet.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SortingGroup))]
    public sealed class WzMapCharacterSorting : MonoBehaviour
    {
        internal static readonly HashSet<WzMapCharacterSorting> Active = new HashSet<WzMapCharacterSorting>();

        [Tooltip("Optional map override. Otherwise the ancestor map or first active map in this scene is used.")]
        public WzMapSorting map;
        public bool automaticFootholdLayer = true;
        [Range(0, 7)] public int mapLayer;

        public int ResolvedMapLayer { get; private set; }
        public SortingGroup Group
        {
            get
            {
                if (sortingGroup == null) sortingGroup = GetComponent<SortingGroup>();
                return sortingGroup;
            }
        }

        private SortingGroup sortingGroup;
        private WzMapSorting resolvedMap;
        private WzMapSorting previousExplicitMap;
        private WzMapSorting footholdMap;
        private readonly List<GameObject> sceneRoots = new List<GameObject>();
        private readonly List<WzMapSorting> mapCandidates = new List<WzMapSorting>();
        private readonly List<WzFootholdInfo> footholds = new List<WzFootholdInfo>();
        private readonly int[] containerOrders = new int[8];
        private bool mapCacheDirty = true;
        private bool refreshNeeded = true;
        private bool layerInitialized;
        private int cachedSceneHandle;
        private float pixelsPerUnit = 100;
        private Vector3 lastPosition;
        private double nextMapLookupTime;

        private void OnEnable()
        {
            Active.Add(this);
            mapCacheDirty = refreshNeeded = true;
            layerInitialized = false;
        }

        private void OnDisable() => Active.Remove(this);
        private void OnDestroy() => Active.Remove(this);

        private void OnValidate()
        {
            // Validation can run while Unity is loading an asset; defer hierarchy changes.
            mapCacheDirty = refreshNeeded = true;
            layerInitialized = false;
            footholdMap = null;
        }

        private void OnTransformParentChanged()
        {
            mapCacheDirty = refreshNeeded = true;
        }

        private void LateUpdate()
        {
            var selectedMap = ResolveMap();
            ConfigureGroup(selectedMap == null);
            if (!Application.isPlaying && (refreshNeeded || transform.position != lastPosition))
            {
                if (selectedMap != null) selectedMap.Refresh();
            }
            lastPosition = transform.position;
            refreshNeeded = false;
        }

        public bool BelongsTo(WzMapSorting candidate)
        {
            if (!isActiveAndEnabled || candidate == null || !candidate.isActiveAndEnabled) return false;
            if (map != null) return map == candidate;
            if (candidate.gameObject.scene != gameObject.scene) return false;
            // A map may have appeared after this actor's first lookup returned no map.
            if (resolvedMap == null) mapCacheDirty = true;
            return ResolveMap() == candidate;
        }

        public int ResolveContainerOrder(WzMapSorting candidate)
        {
            ConfigureGroup(false);
            if (!layerInitialized || !automaticFootholdLayer)
            {
                ResolvedMapLayer = Mathf.Clamp(mapLayer, 0, 7);
                layerInitialized = true;
            }

            if (candidate == null) return 0;
            CacheFootholds(candidate);
            if (automaticFootholdLayer)
            {
                Vector3 feet = candidate.transform.InverseTransformPoint(transform.position);
                float sourceX = feet.x * pixelsPerUnit;
                float sourceY = -feet.y * pixelsPerUnit;
                float tolerance = .05f * pixelsPerUnit;
                float nearestDistance = float.PositiveInfinity;
                int nearestLayer = ResolvedMapLayer;
                foreach (var info in footholds)
                {
                    if (info == null || info.source == null) continue;
                    var source = info.source;
                    float dx = source.x2 - source.x1;
                    // Source footholds running right-to-left are walls/undersides too.
                    if (dx <= .0001f) continue;
                    if (sourceX < Mathf.Min(source.x1, source.x2) || sourceX > Mathf.Max(source.x1, source.x2)) continue;
                    float t = (sourceX - source.x1) / dx;
                    float belowFeet = Mathf.Lerp(source.y1, source.y2, t) - sourceY;
                    if (belowFeet < -tolerance) continue;
                    float distance = Mathf.Abs(belowFeet);
                    int layer = Mathf.Clamp(source.layer, 0, 7);
                    if (distance < nearestDistance - .0001f ||
                        (Mathf.Abs(distance - nearestDistance) <= .0001f && layer == ResolvedMapLayer))
                    {
                        nearestDistance = distance;
                        nearestLayer = layer;
                    }
                }
                // Keep the last layer while jumping away from every foothold.
                ResolvedMapLayer = nearestLayer;
            }
            return containerOrders[ResolvedMapLayer];
        }

        /// <summary>Call after changing the map hierarchy or foothold metadata at runtime.</summary>
        public void InvalidateMapCache()
        {
            mapCacheDirty = refreshNeeded = true;
            footholdMap = null;
        }

        public void Refresh()
        {
            InvalidateMapCache();
            var selectedMap = ResolveMap();
            ConfigureGroup(selectedMap == null);
            if (selectedMap != null) selectedMap.Refresh();
            lastPosition = transform.position;
            refreshNeeded = false;
        }

        private void ConfigureGroup(bool noMap)
        {
            var group = Group;
            if (group == null) return;
            if (!group.enabled) group.enabled = true;
            if (!group.sortAtRoot) group.sortAtRoot = true;
            if (group.sortingLayerID != 0) group.sortingLayerID = 0;
            if (noMap && group.sortingOrder != 0) group.sortingOrder = 0;
        }

        private WzMapSorting ResolveMap()
        {
            if (previousExplicitMap != map || cachedSceneHandle != gameObject.scene.handle)
                mapCacheDirty = true;
            previousExplicitMap = map;
            cachedSceneHandle = gameObject.scene.handle;

            if (map != null)
            {
                resolvedMap = map.isActiveAndEnabled ? map : null;
                mapCacheDirty = false;
                return resolvedMap;
            }
            if (!mapCacheDirty && resolvedMap != null && resolvedMap.isActiveAndEnabled &&
                resolvedMap.gameObject.scene == gameObject.scene) return resolvedMap;
            if (!mapCacheDirty && resolvedMap == null && Time.realtimeSinceStartupAsDouble < nextMapLookupTime)
                return null;

            mapCacheDirty = false;
            nextMapLookupTime = Time.realtimeSinceStartupAsDouble + 1;
            resolvedMap = null;
            for (var parent = transform.parent; parent != null; parent = parent.parent)
            {
                var ancestor = parent.GetComponent<WzMapSorting>();
                if (ancestor == null || !ancestor.isActiveAndEnabled) continue;
                resolvedMap = ancestor;
                return resolvedMap;
            }

            var scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded) return null;
            sceneRoots.Clear();
            scene.GetRootGameObjects(sceneRoots);
            foreach (var root in sceneRoots)
            {
                mapCandidates.Clear();
                root.GetComponentsInChildren(false, mapCandidates);
                foreach (var candidate in mapCandidates)
                {
                    if (!candidate.isActiveAndEnabled) continue;
                    resolvedMap = candidate;
                    return resolvedMap;
                }
            }
            return null;
        }

        private void CacheFootholds(WzMapSorting candidate)
        {
            if (footholdMap == candidate) return;
            footholdMap = candidate;
            footholds.Clear();
            candidate.GetComponentsInChildren(true, footholds);
            var sourceMap = candidate.GetComponent<WzMapScene>();
            pixelsPerUnit = sourceMap != null ? Mathf.Max(.0001f, sourceMap.pixelsPerUnit) : 100;
            for (int layer = 0; layer < containerOrders.Length; layer++)
            {
                // Back, then Obj / Reactor / Tile and each foothold container per layer.
                int count = 0;
                foreach (var info in footholds)
                    if (info != null && info.source != null && info.source.layer <= layer) count++;
                containerOrders[layer] = 3 + 3 * layer + count;
            }
        }
    }
}
