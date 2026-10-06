using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WzUnity;

namespace WzComparerR2.Unity.Editor
{
    internal static class WzSceneBuilder
    {
        public static Vector2 Point(float x, float y, float pixelsPerUnit) => new Vector2(x / pixelsPerUnit, -y / pixelsPerUnit);

        public static void Build(WzUnityManifest manifest, Dictionary<string, GameObject> prefabs, string scenePath)
        {
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                float ppu = manifest.pixelsPerUnit;
                var source = manifest.map;
                var root = new GameObject("Map " + source.id);
                var info = root.AddComponent<WzMapScene>();
                info.mapId = source.id;
                info.sourcePath = manifest.sourcePath;
                info.pixelsPerUnit = ppu;
                info.sourceBounds = new Rect(source.left, source.top, source.width, source.height);
                var sorter = root.AddComponent<WzMapSorting>();
                var groups = new Dictionary<string, Transform>(StringComparer.Ordinal);
                foreach (string name in new[] { "Backgrounds", "Tiles", "Objects", "Foregrounds", "Footholds", "Ladders and Ropes", "Portals", "Life" })
                    groups[name] = Child(root.transform, name);
                var cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                var camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = 5.4f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                var centre = Point(source.left + source.width / 2, source.top + source.height / 2, ppu);
                cameraObject.transform.position = new Vector3(centre.x, centre.y, -10);

                foreach (var layer in source.layers)
                {
                    string groupName = GroupName(layer.group);
                    if (!groups.TryGetValue(groupName, out var group)) groups[groupName] = group = Child(root.transform, groupName);
                    var layerObject = Child(group, layer.id).gameObject;
                    layerObject.transform.localPosition = Point(layer.x, layer.y, ppu);
                    SetOrder(layerObject, layer.containerOrder, layer.z, layer.order);
                    var behaviour = layerObject.AddComponent<WzMapLayerBehaviour>();
                    behaviour.source = layer;
                    behaviour.pixelsPerUnit = ppu;
                    behaviour.viewCamera = camera;
                    behaviour.isBackground = layer.background != null;
                    if (string.IsNullOrEmpty(layer.entityId)) continue;
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[layer.entityId], scene);
                    instance.transform.SetParent(layerObject.transform, false);
                    var animator = instance.GetComponent<WzSpriteAnimator>();
                    animator.FlipX = layer.motion == null || layer.motion.scaleX == null ? layer.flip : false;
                    animator.Opacity = layer.alpha;
                    animator.startingAction = string.IsNullOrEmpty(layer.action) ? animator.animationSet.defaultAction : layer.action;
                    animator.Play(animator.startingAction);
                    if (layer.background != null || layer.motion != null)
                    {
                        behaviour.prototype = animator;
                        behaviour.repeatBounds = SpriteBounds(animator.animationSet);
                        behaviour.repeatSizePixels = behaviour.repeatBounds.size * ppu;
                        // Import previews include repeats; the component keeps them aligned to the camera at runtime.
                        behaviour.Refresh(0);
                    }
                    else behaviour.enabled = false;
                    RecordAnimatorOverrides(animator);
                }
                foreach (var foothold in source.footholds)
                {
                    var item = Child(groups["Footholds"], foothold.id.ToString()).gameObject;
                    item.AddComponent<WzFootholdInfo>().source = foothold;
                    var start = Point(foothold.x1, foothold.y1, ppu);
                    var end = Point(foothold.x2, foothold.y2, ppu);
                    if ((end - start).sqrMagnitude <= 0) continue;
                    var edge = item.AddComponent<EdgeCollider2D>();
                    edge.points = new[] { start, end };
                    if (!Mathf.Approximately(foothold.x1, foothold.x2))
                    {
                        var effector = item.AddComponent<PlatformEffector2D>();
                        effector.useOneWay = true;
                        effector.useOneWayGrouping = true;
                        effector.surfaceArc = 180;
                        edge.usedByEffector = true;
                    }
                }
                foreach (var ladder in source.ladders)
                {
                    var item = Child(groups["Ladders and Ropes"], (ladder.ladder ? "Ladder " : "Rope ") + ladder.id).gameObject;
                    item.transform.localPosition = Point(ladder.x, (ladder.y1 + ladder.y2) / 2, ppu);
                    item.AddComponent<WzLadderInfo>().source = ladder;
                    var trigger = item.AddComponent<BoxCollider2D>();
                    trigger.isTrigger = true;
                    trigger.size = new Vector2(16 / ppu, Mathf.Max(1, Mathf.Abs(ladder.y2 - ladder.y1)) / ppu);
                }
                foreach (var portal in source.portals)
                {
                    var item = Child(groups["Portals"], portal.id + " " + portal.name).gameObject;
                    item.transform.localPosition = Point(portal.x, portal.y, ppu);
                    item.AddComponent<WzPortalInfo>().source = portal;
                    var trigger = item.AddComponent<BoxCollider2D>();
                    trigger.isTrigger = true;
                    trigger.size = new Vector2(40 / ppu, 80 / ppu);
                    trigger.offset = new Vector2(0, 40 / ppu);
                }
                foreach (var placement in source.placements)
                {
                    var item = string.IsNullOrEmpty(placement.entityId) ? new GameObject() : (GameObject)PrefabUtility.InstantiatePrefab(prefabs[placement.entityId], scene);
                    item.name = placement.kind + " " + placement.id;
                    item.transform.SetParent(groups["Life"], false);
                    item.transform.localPosition = Point(placement.x, placement.y, ppu);
                    item.AddComponent<WzPlacementInfo>().source = placement;
                    SetOrder(item, placement.containerOrder, 0, placement.order);
                    var animator = item.GetComponent<WzSpriteAnimator>();
                    if (animator != null)
                    {
                        animator.FlipX = placement.flip;
                        animator.startingAction = string.IsNullOrEmpty(placement.action) ? animator.animationSet.defaultAction : placement.action;
                        animator.Play(animator.startingAction);
                        RecordAnimatorOverrides(animator);
                    }
                    item.SetActive(!placement.hide);
                    if (animator != null) PrefabUtility.RecordPrefabInstancePropertyModifications(item.transform);
                }
                sorter.Refresh();
                foreach (var animator in root.GetComponentsInChildren<WzSpriteAnimator>(true))
                    if (PrefabUtility.IsPartOfPrefabInstance(animator.gameObject)) RecordAnimatorOverrides(animator);
                if (!EditorSceneManager.SaveScene(scene, scenePath)) throw new IOException("Unity could not save map scene: " + scenePath);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static Transform Child(Transform parent, string name)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            return child;
        }

        private static void SetOrder(GameObject gameObject, int container, int z, int order)
        {
            var component = gameObject.AddComponent<WzMapRenderOrder>();
            component.containerOrder = container;
            component.baseZ = z;
            component.sourceOrder = order;
        }

        private static void RecordAnimatorOverrides(WzSpriteAnimator animator)
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
            PrefabUtility.RecordPrefabInstancePropertyModifications(animator.gameObject);
            foreach (var fragment in animator.GetComponentsInChildren<WzRenderFragment>(true))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(fragment.transform);
                var renderer = fragment.GetComponent<SpriteRenderer>();
                if (renderer != null) PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
        }

        private static string GroupName(string source)
        {
            switch ((source ?? "").ToLowerInvariant())
            {
                case "back": case "background": case "backgrounds": return "Backgrounds";
                case "tile": case "tiles": return "Tiles";
                case "obj": case "object": case "objects": return "Objects";
                case "front": case "foreground": case "foregrounds": return "Foregrounds";
                default: return string.IsNullOrEmpty(source) ? "Objects" : source;
            }
        }

        private static Rect SpriteBounds(WzAnimationSet set)
        {
            bool any = false;
            var min = Vector2.zero;
            var max = Vector2.zero;
            foreach (var action in set.actions)
            foreach (var track in action.tracks)
            foreach (var frame in track.frames)
            foreach (var slice in frame.slices)
            {
                if (slice.sprite == null) continue;
                var half = slice.sprite.rect.size / (2 * set.pixelsPerUnit);
                if (!any) { min = slice.position - half; max = slice.position + half; any = true; }
                else { min = Vector2.Min(min, slice.position - half); max = Vector2.Max(max, slice.position + half); }
            }
            return any ? new Rect(min, max - min) : new Rect(Vector2.zero, Vector2.one);
        }
    }
}
