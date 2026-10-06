using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using WzUnity;
using Newtonsoft.Json;

namespace WzComparerR2.Unity.Editor
{
    public static class WzRealValidation
    {
        [Serializable] public sealed class Report
        {
            public bool passed;
            public string unityVersion;
            public List<string> checks = new List<string>();
            public List<string> images = new List<string>();
            public List<string> errors = new List<string>();
            public int checkedFrames;
            public int checkedRenderers;
            public int checkedMaps;
            public int playerFrames;
            public double playTimeSeconds;
            public string visualComparison = "Captured PNG RGB can differ from source compositing because the existing Unity project uses Linear color space and URP blends translucent layers in linear light. Render targets also contain premultiplied RGB. No RGB pixel-equality claim is made.";
            public List<ImageComparison> avatarImageComparisons = new List<ImageComparison>();
        }
        [Serializable] public sealed class ImageComparison
        {
            public string action;
            public double timeMs;
            public bool boundsMatch;
            public int alphaDifferentPixels;
            public int rgbDifferentOver2Pixels;
        }
        private const string Results = "Library/WzImporterValidation";

        public static Report Run(string exportRoot, bool capture = true)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Run real import validation outside Play Mode.");
            var report = new Report { unityVersion = Application.unityVersion };
            Directory.CreateDirectory(Results);
            string activePath = SceneManager.GetActiveScene().path;
            bool dirty = SceneManager.GetActiveScene().isDirty;
            foreach (string folder in Directory.GetDirectories(exportRoot).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(folder, "wz-unity.json"))) continue;
                try
                {
                    var manifest = WzBundleValidator.ReadAndValidate(folder);
                    string imported = WzUnityImporter.OutputRoot + "/" + WzUnityImporter.StableName(manifest.id);
                    if (!File.Exists(imported + "/import-result.json")) { report.checks.Add("SKIPPED unimported source " + manifest.id); continue; }
                    if (manifest.map != null)
                    {
                        CheckMap(manifest, imported, capture, report);
                        var mobIds = new HashSet<string>(manifest.map.placements.Where(item => item.kind == "mob" && !string.IsNullOrEmpty(item.entityId)).Select(item => item.entityId));
                        var mobs = manifest.entities.Where(entity => mobIds.Contains(entity.id)).ToList();
                        if (mobs.Count > 0)
                            CheckEntities(new WzUnityManifest { id = manifest.id, kind = "mob", pixelsPerUnit = manifest.pixelsPerUnit, assets = manifest.assets, entities = mobs }, imported, false, null, report);
                    }
                    else CheckEntities(manifest, imported, capture && manifest.kind == "avatar", Path.Combine(exportRoot, "avatar-reference"), report);
                }
                catch (Exception exception) { report.errors.Add(Path.GetFileName(folder) + ": " + exception); }
            }
            if (SceneManager.GetActiveScene().path != activePath || SceneManager.GetActiveScene().isDirty != dirty) report.errors.Add("User scene state changed during real validation.");
            report.passed = report.errors.Count == 0;
            File.WriteAllText(Results + "/real-validation.json", JsonUtility.ToJson(report, true));
            return report;
        }

        private static void CheckMap(WzUnityManifest manifest, string imported, bool capture, Report report)
        {
            string path = imported + "/Scenes/" + WzUnityImporter.StableName(manifest.map.id) + ".unity";
            var scene = EditorSceneManager.OpenPreviewScene(path);
            try
            {
                var source = manifest.map;
                var roots = scene.GetRootGameObjects();
                var root = roots.Single(item => item.GetComponent<WzMapScene>() != null);
                Require(root.GetComponent<WzMapScene>().mapId == source.id, "Map identity mismatch.");
                CheckMissingReferences(root);
                var footholds = root.GetComponentsInChildren<WzFootholdInfo>(true);
                Require(footholds.Length == source.footholds.Count, "Foothold count differs from manifest.");
                var byFoothold = footholds.ToDictionary(item => item.source.id);
                foreach (var item in source.footholds)
                {
                    var component = byFoothold[item.id];
                    Require(component.source.prev == item.prev && component.source.next == item.next && component.source.layer == item.layer && component.source.group == item.group && component.source.forbidFallDown == item.forbidFallDown, "Foothold links/metadata changed: " + item.id);
                    var edge = component.GetComponent<EdgeCollider2D>();
                    if (item.x1 == item.x2 && item.y1 == item.y2) continue;
                    Require(edge != null && edge.points.Length == 2, "Missing foothold collider: " + item.id);
                    Near(edge.points[0], Point(item.x1, item.y1, manifest.pixelsPerUnit), "Foothold start");
                    Near(edge.points[1], Point(item.x2, item.y2, manifest.pixelsPerUnit), "Foothold end");
                    Require((component.GetComponent<PlatformEffector2D>() != null) == (item.x1 != item.x2), "Wall/floor effector mismatch: " + item.id);
                }
                var ladders = root.GetComponentsInChildren<WzLadderInfo>(true);
                Require(ladders.Length == source.ladders.Count, "Ladder count differs from manifest.");
                foreach (var item in source.ladders)
                {
                    var actual = ladders.Single(candidate => candidate.source.id == item.id);
                    Near(actual.transform.localPosition, Point(item.x, (item.y1 + item.y2) / 2, manifest.pixelsPerUnit), "Ladder position");
                    Require(actual.source.ladder == item.ladder && actual.source.upperFoothold == item.upperFoothold && actual.GetComponent<BoxCollider2D>().isTrigger, "Ladder metadata/trigger changed.");
                }
                var portals = root.GetComponentsInChildren<WzPortalInfo>(true);
                Require(portals.Length == source.portals.Count, "Portal count differs from manifest.");
                foreach (var item in source.portals)
                {
                    var actual = portals.Single(candidate => candidate.source.id == item.id);
                    Near(actual.transform.localPosition, Point(item.x, item.y, manifest.pixelsPerUnit), "Portal position");
                    Require(Equal(actual.source.targetMap, item.targetMap) && Equal(actual.source.targetName, item.targetName) && actual.source.type == item.type && Equal(actual.source.script, item.script), "Portal metadata changed.");
                }
                var placements = root.GetComponentsInChildren<WzPlacementInfo>(true);
                Require(placements.Length == source.placements.Count, "Life placement count differs from manifest.");
                foreach (var item in source.placements)
                {
                    var actual = placements.Single(candidate => candidate.source.id == item.id && candidate.source.kind == item.kind);
                    Near(actual.transform.localPosition, Point(item.x, item.y, manifest.pixelsPerUnit), "Life source placement");
                    Require(actual.source.originalY == item.originalY && actual.source.cy == item.cy && actual.source.foothold == item.foothold && actual.source.spawnTime == item.spawnTime && actual.source.rx0 == item.rx0 && actual.source.rx1 == item.rx1 && actual.source.flip == item.flip, "Life metadata changed.");
                    Require(actual.gameObject.activeSelf == !item.hide, "Life hide flag changed.");
                    var animator = actual.GetComponent<WzSpriteAnimator>();
                    if (string.IsNullOrEmpty(item.entityId)) Require(animator == null, "Unsupported source should be a metadata placeholder.");
                    else
                    {
                        Require(animator != null && PrefabUtility.IsPartOfPrefabInstance(actual.gameObject), "Life prefab link missing.");
                        Require(animator.FlipX == item.flip && animator.animationSet.sourceId == item.entityId, "Life direction/entity mismatch.");
                    }
                }
                var layers = root.GetComponentsInChildren<WzMapLayerBehaviour>(true);
                Require(layers.Length == source.layers.Count, "Map layer count differs from manifest.");
                var sourceLayers = source.layers.ToDictionary(layer => layer.id);
                var camera = roots.SelectMany(item => item.GetComponentsInChildren<Camera>(true)).Single();
                camera.scene = scene;
                camera.aspect = 16f / 9f;
                Vector3 startCamera = camera.transform.position;
                int snapshot = 0;
                foreach (double time in new[] { 0d, 5000d, 17000d })
                {
                    camera.transform.position = startCamera + new Vector3(snapshot * 1.275f, snapshot * -0.5375f, 0);
                    foreach (var layer in layers)
                    {
                        var original = sourceLayers[layer.source.id];
                        layer.viewCamera = camera;
                        if (layer.enabled) layer.Refresh(time);
                        float x = original.x + Motion(original.motion == null ? null : original.motion.x, time, 0);
                        float y = original.y + Motion(original.motion == null ? null : original.motion.y, time, 0);
                        if (original.background != null && !string.IsNullOrEmpty(original.entityId))
                        {
                            var bg = original.background;
                            float cx = camera.transform.position.x * manifest.pixelsPerUnit;
                            float cy = -camera.transform.position.y * manifest.pixelsPerUnit;
                            if (bg.scrollX)
                            {
                                double offset = (double)bg.rx * Math.Max(1, bg.scrollDistanceX) * time / 20000;
                                float tile = Math.Max(1, bg.cx != 0 ? Math.Abs(bg.cx) : layer.repeatSizePixels.x);
                                x += (float)(bg.repeatX ? offset % tile : offset);
                            }
                            else if (bg.parallax) x += cx * (100 + bg.rx) / 100;
                            if (bg.scrollY)
                            {
                                double offset = (double)bg.ry * Math.Max(1, bg.scrollDistanceY) * time / 20000;
                                float tile = Math.Max(1, bg.cy != 0 ? Math.Abs(bg.cy) : layer.repeatSizePixels.y);
                                y += (float)(bg.repeatY ? offset % tile : offset);
                            }
                            else if (bg.parallax) y += cy * (100 + bg.ry) / 100;
                            x = Mathf.Floor(x); y = Mathf.Floor(y);
                            if (bg.repeatX || bg.repeatY)
                            {
                                var tiles = layer.GetComponentsInChildren<WzSpriteAnimator>(false);
                                Require(tiles.Length > 0, "Repeating background has no visible tiles.");
                                foreach (var tile in tiles)
                                {
                                    if (bg.repeatX) Near(tile.transform.localPosition.x / (Math.Max(1, Math.Abs(bg.cx)) / manifest.pixelsPerUnit), Mathf.Round(tile.transform.localPosition.x / (Math.Max(1, Math.Abs(bg.cx)) / manifest.pixelsPerUnit)), "Horizontal repeat grid");
                                    if (bg.repeatY) Near(tile.transform.localPosition.y / (Math.Max(1, Math.Abs(bg.cy)) / manifest.pixelsPerUnit), Mathf.Round(tile.transform.localPosition.y / (Math.Max(1, Math.Abs(bg.cy)) / manifest.pixelsPerUnit)), "Vertical repeat grid");
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(original.entityId)) Near(layer.transform.localPosition, Point(x, y, manifest.pixelsPerUnit), "Map movement/parallax at " + time);
                    }
                    foreach (var animator in root.GetComponentsInChildren<WzSpriteAnimator>(true))
                    {
                        animator.Sample(string.IsNullOrEmpty(animator.startingAction) ? animator.animationSet.defaultAction : animator.startingAction, time);
                        animator.Stop();
                    }
                    root.GetComponent<WzMapSorting>().Refresh();
                    CheckMapSorting(root);
                    if (capture)
                    {
                        string image = Results + "/map-" + source.id + "-" + (long)time + ".png";
                        Capture(camera, 1280, 720, image);
                        report.images.Add(image);
                    }
                    snapshot++;
                }
                report.checkedMaps++;
                report.checks.Add("Map " + source.id + ": " + layers.Length + " layers, " + footholds.Length + " footholds, " + placements.Length + " placements; source metadata, refs, ordering and camera/animation at 0/5000/17000ms passed");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static void CheckEntities(WzUnityManifest manifest, string imported, bool capture, string referenceFolder, Report report)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var assets = manifest.assets.ToDictionary(asset => asset.id);
                foreach (var entity in manifest.entities)
                {
                    string path = imported + "/Prefabs/" + WzUnityImporter.StableName(entity.id) + ".prefab";
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    Require(prefab != null, "Missing prefab " + path);
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    try
                    {
                        var animator = instance.GetComponent<WzSpriteAnimator>();
                        Require(animator != null && animator.animationSet != null, "Prefab animation references missing.");
                        CheckMissingReferences(instance);
                        Require(animator.animationSet.actions.Length == entity.clips.Count, "Action count differs from source.");
                        foreach (var clip in entity.clips)
                        {
                            var sampleTimes = new SortedSet<double> { 0, 137, 489 };
                            foreach (var track in clip.tracks)
                            {
                                double at = track.startMs;
                                foreach (var frame in track.frames) { sampleTimes.Add(at + Math.Min(0.1, frame.delayMs / 4)); sampleTimes.Add(at + frame.delayMs / 2); at += frame.delayMs; }
                            }
                            animator.UseAuthoredLoop();
                            animator.FlipX = false;
                            animator.Play(clip.name);
                            foreach (double time in sampleTimes)
                            {
                                animator.Sample(clip.name, time);
                                CheckPose(manifest, clip, assets, animator, time, report);
                            }
                            if (!clip.loop)
                            {
                                int complete = 0;
                                Action<string> onComplete = _ => complete++;
                                animator.Completed += onComplete;
                                animator.Play(clip.name);
                                animator.Advance((float)(clip.durationMs / 1000 + 0.1));
                                animator.Advance(1);
                                Require(complete == 1 && !animator.IsPlaying, "Non-looping action failed completion: " + clip.name);
                                animator.Completed -= onComplete;
                            }
                            if (capture)
                            foreach (double time in new[] { 0d, 137d, 489d })
                            {
                                animator.Sample(clip.name, time);
                                animator.Stop();
                                var enabled = instance.GetComponentsInChildren<SpriteRenderer>(true).Where(renderer => renderer.enabled && renderer.sprite != null).ToArray();
                                if (enabled.Length == 0) continue;
                                Bounds bounds = enabled[0].bounds;
                                foreach (var renderer in enabled.Skip(1)) bounds.Encapsulate(renderer.bounds);
                                float ppu = manifest.pixelsPerUnit;
                                int left = Mathf.FloorToInt(bounds.min.x * ppu + 0.001f);
                                int top = Mathf.FloorToInt(-bounds.max.y * ppu + 0.001f);
                                int width = Mathf.CeilToInt(bounds.max.x * ppu - 0.001f) - left;
                                int height = Mathf.CeilToInt(-bounds.min.y * ppu - 0.001f) - top;
                                var cameraObject = new GameObject("WZ Validation Camera");
                                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                                try
                                {
                                    var camera = cameraObject.AddComponent<Camera>();
                                    camera.scene = scene;
                                    camera.orthographic = true;
                                    camera.orthographicSize = height / (2 * ppu);
                                    camera.aspect = width / (float)height;
                                    camera.clearFlags = CameraClearFlags.SolidColor;
                                    camera.backgroundColor = Color.clear;
                                    camera.transform.position = new Vector3((left + width / 2f) / ppu, -(top + height / 2f) / ppu, -10);
                                    string image = Results + "/avatar-" + clip.name + "-" + (long)time + "-unity.png";
                                    Capture(camera, width, height, image);
                                    File.WriteAllText(Path.ChangeExtension(image, ".json"), JsonConvert.SerializeObject(new { left, top, width, height, action = clip.name, timeMs = time }));
                                    report.images.Add(image);
                                    CompareAvatarImage(referenceFolder, image, clip.name, time, left, top, width, height, report);
                                }
                                finally { UnityEngine.Object.DestroyImmediate(cameraObject); }
                            }
                        }
                        report.checks.Add(entity.kind + " " + entity.id + ": all " + entity.clips.Count + " actions sampled at every source frame boundary/midpoint; pose, sprite, alpha, order and non-looping completion passed");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(instance); }
                }
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static void CheckPose(WzUnityManifest manifest, WzUnity.WzAnimationClip clip, Dictionary<string, WzPngAsset> assets, WzSpriteAnimator animator, double time, Report report)
        {
            var fragments = animator.GetComponentsInChildren<WzRenderFragment>(true);
            int visible = 0;
            foreach (var track in clip.tracks)
            {
                int index = FrameIndex(track, clip.loop, time, out double fraction);
                var frame = track.frames[index];
                var renderer = fragments.Single(fragment => fragment.key.StartsWith(track.id + "\n", StringComparison.Ordinal)).GetComponent<SpriteRenderer>();
                WzTrackPose pose = null;
                if (!string.IsNullOrEmpty(track.poseTrack))
                {
                    var clock = clip.tracks.Single(item => item.id == track.poseTrack);
                    int poseFrame = FrameIndex(clock, clip.loop, time, out _);
                    pose = track.poses.FirstOrDefault(candidate => candidate.poseFrame == poseFrame && candidate.frameIndex == index) ?? track.poses.FirstOrDefault(candidate => candidate.poseFrame == poseFrame && candidate.frameIndex == -1);
                }
                string assetId = pose != null && pose.overrideSprite ? pose.assetId : frame.assetId;
                bool expectedVisible = time >= track.startMs && !string.IsNullOrEmpty(assetId) && (pose == null ? frame.visible : pose.visible);
                Require(renderer.enabled == expectedVisible, "Visibility mismatch: " + clip.name + "/" + track.id + " @ " + time);
                if (!expectedVisible) continue;
                visible++;
                var asset = assets[assetId];
                Require(renderer.sprite != null && renderer.sprite.rect.width == asset.width && renderer.sprite.rect.height == asset.height, "Sprite dimensions mismatch.");
                string expectedFile = (string.IsNullOrEmpty(asset.sha256) ? WzUnityImporter.StableName(asset.id) : asset.sha256.ToLowerInvariant()) + ".png";
                Require(Path.GetFileName(AssetDatabase.GetAssetPath(renderer.sprite)) == expectedFile, "Pose sprite mismatch: " + clip.name + "/" + track.id);
                float anchorX = pose == null ? frame.x : pose.x;
                float anchorY = pose == null ? frame.y : pose.y;
                float originX = pose != null && pose.overrideSprite ? pose.originX : frame.originX;
                float originY = pose != null && pose.overrideSprite ? pose.originY : frame.originY;
                Vector2 offset = Point((asset.width / 2f - originX) * frame.scaleX, (asset.height / 2f - originY) * frame.scaleY, manifest.pixelsPerUnit);
                Vector2 expectedPosition = Point(anchorX, anchorY, manifest.pixelsPerUnit) + (Vector2)(Quaternion.Euler(0, 0, -frame.rotation) * offset);
                Near(renderer.transform.localPosition, expectedPosition, "Frame/pose anchor " + clip.name + "/" + track.id + " @ " + time);
                Near(renderer.color.a, (float)((frame.a0 + (frame.a1 - frame.a0) * fraction) / 255d), "Frame alpha");
                var sourceOrder = animator.SourceOrders.Single(pair => pair.Key == renderer).Value;
                Require(sourceOrder.x == (pose == null ? frame.z : pose.z) && sourceOrder.y == (pose == null ? frame.drawOrder : pose.drawOrder), "Per-frame Z/draw order mismatch.");
                report.checkedRenderers++;
            }
            Require(animator.GetComponentsInChildren<SpriteRenderer>(true).Count(renderer => renderer.enabled) == visible, "Old action left visible renderer fragments behind.");
            report.checkedFrames++;
        }

        private static int FrameIndex(WzUnity.WzAnimationTrack track, bool actionLoop, double time, out double fraction)
        {
            double duration = track.frames.Sum(frame => frame.delayMs);
            double elapsed = Math.Max(0, time - track.startMs);
            elapsed = actionLoop && track.loop ? elapsed % duration : Math.Min(elapsed, duration);
            for (int index = 0; index < track.frames.Count; index++)
            {
                if (elapsed < track.frames[index].delayMs || index == track.frames.Count - 1)
                { fraction = Math.Min(1, elapsed / track.frames[index].delayMs); return index; }
                elapsed -= track.frames[index].delayMs;
            }
            fraction = 1;
            return track.frames.Count - 1;
        }

        private static float Motion(WzMotionChannel channel, double time, float fallback)
        {
            if (channel == null || string.IsNullOrEmpty(channel.kind)) return fallback;
            time = Math.Max(0, time + channel.phaseMs);
            if (channel.cycleMs > 0) time = channel.loop ? time % channel.cycleMs : Math.Min(time, channel.cycleMs);
            float value = channel.offset;
            if (channel.kind == "sine") value += channel.amplitude * (float)Math.Sin(channel.cycleMs > 0 ? time * 2 * Math.PI / channel.cycleMs : 0);
            else if (channel.kind == "cosine") value += channel.amplitude * (float)Math.Cos(channel.cycleMs > 0 ? time * 2 * Math.PI / channel.cycleMs : 0);
            else if (channel.kind == "keyframes" && channel.keys.Count > 0)
            {
                int left = 0;
                while (left + 1 < channel.keys.Count && time >= channel.keys[left + 1].timeMs) left++;
                var key = channel.keys[left];
                value = key.value;
                if (left + 1 < channel.keys.Count && key.interpolation != "hold")
                {
                    var next = channel.keys[left + 1];
                    value = Mathf.Lerp(key.value, next.value, (float)((time - key.timeMs) / Math.Max(0.001, next.timeMs - key.timeMs)));
                }
            }
            return channel.pixelSnap ? Mathf.Round(value) : value;
        }

        private static void CheckMissingReferences(GameObject root)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                Require(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) == 0, "Missing script on " + transform.name);
            foreach (var animator in root.GetComponentsInChildren<WzSpriteAnimator>(true))
            {
                Require(animator.animationSet != null && animator.spriteMaterial != null && animator.spriteMaterial.shader != null, "Missing animation/material reference.");
                foreach (var action in animator.animationSet.actions)
                foreach (var track in action.tracks)
                foreach (var frame in track.frames)
                foreach (var slice in frame.slices)
                    Require(!slice.visible || slice.sprite != null, "Visible frame has a missing sprite reference.");
            }
        }

        private static void CheckMapSorting(GameObject root)
        {
            var rows = root.GetComponentsInChildren<WzSpriteAnimator>(false).SelectMany(animator =>
            {
                var owner = animator.GetComponentInParent<WzMapRenderOrder>();
                return animator.SourceOrders.Where(pair => pair.Key.enabled && pair.Key.gameObject.activeInHierarchy).Select(pair => new { renderer = pair.Key, container = owner.containerOrder, z = owner.baseZ + pair.Value.x, order = owner.sourceOrder, fragment = pair.Value.y });
            }).OrderBy(row => row.container).ThenBy(row => row.z).ThenBy(row => row.order).ThenBy(row => row.fragment).ToArray();
            for (int index = 1; index < rows.Length; index++) Require(rows[index].renderer.sortingOrder >= rows[index - 1].renderer.sortingOrder, "Map renderer order no longer matches source container/Z order.");
        }

        // URP 17's own RuntimeTests uses this supported StandardRequest path; Camera.Render is its built-in fallback.
        private static void Capture(Camera camera, int width, int height, string path)
        {
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D image = null;
            try
            {
                target.Create();
                camera.targetTexture = target;
                camera.aspect = width / (float)height;
                if (GraphicsSettings.currentRenderPipeline != null)
                    RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
                else camera.Render();
                RenderTexture.active = target;
                image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                target.Release(); UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static bool Equal(string left, string right) => (left ?? "") == (right ?? "");

        private static void CompareAvatarImage(string referenceFolder, string imagePath, string action, double time, int left, int top, int width, int height, Report report)
        {
            string prefix = Path.Combine(referenceFolder, action + (time == 0 ? "" : "-" + (long)time));
            if (!File.Exists(prefix + "-source.png")) return;
            var expected = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var actual = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            try
            {
                expected.LoadImage(File.ReadAllBytes(prefix + "-source.png"));
                actual.LoadImage(File.ReadAllBytes(imagePath));
                var comparison = new ImageComparison { action = action, timeMs = time, boundsMatch = expected.width == actual.width && expected.height == actual.height };
                if (File.Exists(prefix + "-bounds.json"))
                {
                    var bounds = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(prefix + "-bounds.json"));
                    comparison.boundsMatch &= (int)bounds["x"] == left && (int)bounds["y"] == top && (int)bounds["width"] == width && (int)bounds["height"] == height;
                }
                Require(comparison.boundsMatch, "Avatar source/Unity bounds mismatch: " + action + " @ " + time);
                var sourcePixels = expected.GetPixels32(); var unityPixels = actual.GetPixels32();
                for (int index = 0; index < sourcePixels.Length; index++)
                {
                    var source = sourcePixels[index]; var unity = unityPixels[index];
                    if (source.a != unity.a) comparison.alphaDifferentPixels++;
                    if ((source.a > 0 || unity.a > 0) && (Math.Abs(source.r - unity.r) > 2 || Math.Abs(source.g - unity.g) > 2 || Math.Abs(source.b - unity.b) > 2)) comparison.rgbDifferentOver2Pixels++;
                }
                report.avatarImageComparisons.Add(comparison);
                Require(comparison.alphaDifferentPixels == 0, "Avatar source/Unity alpha mismatch: " + action + " @ " + time);
            }
            finally { UnityEngine.Object.DestroyImmediate(expected); UnityEngine.Object.DestroyImmediate(actual); }
        }
        private static Vector2 Point(float x, float y, float ppu) => new Vector2(x / ppu, -y / ppu);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static void Near(Vector2 actual, Vector2 expected, string message) { Require(Vector2.Distance(actual, expected) < 0.0001f, message + ": expected " + expected + ", got " + actual); }
        private static void Near(float actual, float expected, string message) { Require(Math.Abs(actual - expected) < 0.0001f, message + ": expected " + expected + ", got " + actual); }

        private sealed class PlaySample { public WzSpriteAnimator animator; public string before; public bool changed; }
        private static List<PlaySample> playSamples;
        private static double playStart;
        private static int playStartFrame;
        private static double playStartTime;
        private static bool priorRunInBackground;
        private static readonly List<string> playErrors = new List<string>();
        public static string BeginPlayValidation()
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Enter Play Mode with an imported map scene first.");
            priorRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
            playSamples = UnityEngine.Object.FindObjectsByType<WzSpriteAnimator>(FindObjectsSortMode.None)
                .Where(animator => animator.animationSet != null && animator.animationSet.actions.Any(action => action.tracks.Any(track => track.frames.Length > 1)))
                .Take(100).Select(animator => new PlaySample { animator = animator, before = Signature(animator) }).ToList();
            Require(playSamples.Count > 0, "No animated imported objects found in Play Mode.");
            playStart = EditorApplication.timeSinceStartup;
            playStartFrame = Time.frameCount;
            playStartTime = Time.timeAsDouble;
            playErrors.Clear();
            Application.logMessageReceived -= ObservePlayLog;
            Application.logMessageReceived += ObservePlayLog;
            EditorApplication.update -= PollPlay;
            EditorApplication.update += PollPlay;
            return "Play Mode observation started for " + playSamples.Count + " animators; requires 30 player frames and 3 gameplay seconds. Poll Library/WzImporterValidation/play-validation.json.";
        }

        private static void PollPlay()
        {
            foreach (var sample in playSamples) if (sample.animator != null && Signature(sample.animator) != sample.before) sample.changed = true;
            if (Application.isPlaying)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                double elapsed = EditorApplication.timeSinceStartup - playStart;
                if (elapsed < 60 && (Time.frameCount - playStartFrame < 30 || Time.timeAsDouble - playStartTime < 3)) return;
            }
            EditorApplication.update -= PollPlay;
            Application.logMessageReceived -= ObservePlayLog;
            Application.runInBackground = priorRunInBackground;
            int changed = playSamples.Count(sample => sample.changed);
            var report = new Report { unityVersion = Application.unityVersion, playerFrames = Time.frameCount - playStartFrame, playTimeSeconds = Time.timeAsDouble - playStartTime };
            if (!Application.isPlaying) report.errors.Add("Play Mode ended before observation finished.");
            if (report.playerFrames < 30 || report.playTimeSeconds < 3) report.errors.Add("Insufficient sustained playback: requires at least 30 player frames and 3 gameplay seconds.");
            report.errors.AddRange(playErrors);
            if (changed == 0) report.errors.Add("No imported animated renderer changed automatically during Play Mode.");
            foreach (var sample in playSamples)
                if (sample.animator != null && sample.animator.GetComponentsInChildren<WzRenderFragment>(true).GroupBy(fragment => fragment.key).Any(group => group.Count() > 1)) report.errors.Add("Duplicate renderer fragments after Play Mode/domain reload: " + sample.animator.name);
            report.checks.Add("Observed " + report.playerFrames + " player frames and " + report.playTimeSeconds.ToString("F3") + " gameplay seconds; " + changed + "/" + playSamples.Count + " imported animators changed visible state; no duplicated fragment keys; " + playErrors.Count + " runtime console errors");
            report.passed = report.errors.Count == 0;
            File.WriteAllText(Results + "/play-validation.json", JsonUtility.ToJson(report, true));
        }

        private static string Signature(WzSpriteAnimator animator) => string.Join(";", animator.GetComponentsInChildren<SpriteRenderer>(false).Where(renderer => renderer.enabled).Select(renderer => (renderer.sprite == null ? "null" : renderer.sprite.GetInstanceID().ToString()) + ":" + renderer.transform.localPosition.ToString("F4") + ":" + renderer.color.a.ToString("F4")));
        private static void ObservePlayLog(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) playErrors.Add(message + "\n" + trace);
        }
    }
}
