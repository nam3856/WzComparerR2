using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WzUnity;

namespace WzComparerR2.Unity.Editor
{
    public static class WzScrollValidation
    {
        [Serializable] public sealed class Report
        {
            public bool passed;
            public int checkedPairs;
            public int checkedLayers;
            public List<string> checks = new List<string>();
            public string error;
        }

        public static void CheckSynthetic()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one / 2, 100);
            var set = ScriptableObject.CreateInstance<WzAnimationSet>();
            set.defaultAction = "stand";
            set.actions = new[] { new WzAnimationAction { name = "stand", tracks = new[] { new WzAnimationTrack { name = "body", frames = new[] { new WzAnimationFrame { slices = new[] { new WzRenderSlice { slot = "body", part = "body", sprite = sprite } } } } } } } };
            try
            {
                foreach (var test in new[]
                {
                    new Case(false, -50, 100, 800, true), new Case(false, 50, 100, 800, true),
                    new Case(true, -33, 100, 1200, true), new Case(true, 33, 100, 1200, true),
                    new Case(false, -40, 250, 900, true), new Case(true, 40, 350, 1700, true),
                    new Case(false, -50, 100, 0, true), new Case(true, 50, 100, 0, true),
                    new Case(false, -50, 100, 800, false), new Case(true, 50, 250, 1200, false)
                })
                {
                    var root = new GameObject("Scrolling regression");
                    SceneManager.MoveGameObjectToScene(root, scene);
                    var cameraObject = new GameObject("Regression camera");
                    SceneManager.MoveGameObjectToScene(cameraObject, scene);
                    try
                    {
                        var camera = cameraObject.AddComponent<Camera>();
                        camera.orthographic = true; camera.orthographicSize = 10; camera.aspect = 1.6f;
                        var child = new GameObject("Prototype"); child.transform.SetParent(root.transform, false);
                        var animator = child.AddComponent<WzSpriteAnimator>(); animator.animationSet = set; animator.Play("stand");
                        var layer = root.AddComponent<WzMapLayerBehaviour>();
                        layer.prototype = animator; layer.viewCamera = camera; layer.pixelsPerUnit = 100; layer.isBackground = true;
                        layer.repeatSizePixels = new Vector2(800, 1200); layer.repeatBounds = new Rect(0, -0.02f, 0.02f, 0.02f);
                        layer.source = new WzMapLayer { id = "synthetic", x = 13, y = 7, background = new WzBackground
                        {
                            rx = test.vertical ? -100 : test.rate, ry = test.vertical ? test.rate : -100,
                            scrollX = !test.vertical, scrollY = test.vertical, repeatX = !test.vertical && test.repeat, repeatY = test.vertical && test.repeat,
                            cx = test.vertical ? 800 : test.spacing, cy = test.vertical ? test.spacing : 1200,
                            scrollDistanceX = test.distance, scrollDistanceY = test.distance
                        } };
                        var report = new Report();
                        CheckLayer(layer, camera, report);
                        // The untouched axis still follows its authored parallax factor.
                        var bg = layer.source.background;
                        if (test.vertical) { bg.rx = -50; bg.scrollX = false; camera.transform.position += new Vector3(3.25f, 0, 0); }
                        else { bg.ry = -50; bg.scrollY = false; camera.transform.position += new Vector3(0, 3.25f, 0); }
                        layer.Refresh(1234);
                        Near(layer.transform.position, ExpectedRoot(layer, camera, 1234, true), "Parallax/pixel snapping changed");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cameraObject); }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(set); UnityEngine.Object.DestroyImmediate(sprite); UnityEngine.Object.DestroyImmediate(texture);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        public static Report CheckImported(string exportDirectory)
        {
            var report = new Report();
            var original = SceneManager.GetActiveScene(); string path = original.path; bool dirty = original.isDirty;
            Scene scene = default;
            try
            {
                var manifest = WzBundleValidator.ReadAndValidate(exportDirectory);
                string name = WzUnityImporter.StableName(manifest.id);
                scene = EditorSceneManager.OpenPreviewScene(WzUnityImporter.OutputRoot + "/" + name + "/Scenes/" + WzUnityImporter.StableName(manifest.map.id) + ".unity");
                var camera = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true)).Single();
                camera.aspect = 16f / 9f;
                foreach (var layer in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<WzMapLayerBehaviour>(true)))
                {
                    if (!layer.isBackground || layer.source.background == null || (!layer.source.background.scrollX && !layer.source.background.scrollY)) continue;
                    layer.viewCamera = camera;
                    CheckLayer(layer, camera, report);
                    report.checkedLayers++;
                    report.checks.Add(layer.source.id + ": speed-basis crossings, true tile wraps and moved-camera visible copies remain continuous");
                }
                if (report.checkedLayers == 0) throw new InvalidOperationException("No scrolling backgrounds found.");
                if (SceneManager.GetActiveScene().path != path || SceneManager.GetActiveScene().isDirty != dirty) throw new InvalidOperationException("User scene state changed.");
                report.passed = true;
            }
            catch (Exception exception) { report.error = exception.ToString(); }
            finally
            {
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                Directory.CreateDirectory("Library/WzImporterValidation");
                File.WriteAllText("Library/WzImporterValidation/scroll-validation.json", JsonUtility.ToJson(report, true));
            }
            return report;
        }

        private sealed class Case
        {
            public bool vertical, repeat; public float rate, distance, spacing;
            public Case(bool vertical, float rate, float distance, float spacing, bool repeat) { this.vertical = vertical; this.rate = rate; this.distance = distance; this.spacing = spacing; this.repeat = repeat; }
        }

        private static void CheckLayer(WzMapLayerBehaviour layer, Camera camera, Report report)
        {
            var bg = layer.source.background;
            var originalCamera = camera.transform.position;
            foreach (bool vertical in new[] { false, true })
            {
                if (vertical ? !bg.scrollY || bg.ry == 0 : !bg.scrollX || bg.rx == 0) continue;
                float rate = vertical ? bg.ry : bg.rx;
                float speedDistance = Math.Max(1, vertical ? bg.scrollDistanceY : bg.scrollDistanceX);
                float tile = TileSize(layer, vertical);
                double oldPeriod = 20000d / Math.Abs(rate);
                double actualPeriod = 20000d * tile / (Math.Abs(rate) * speedDistance);
                foreach (double crossing in new[] { oldPeriod, oldPeriod * 2, actualPeriod, actualPeriod * 3 })
                {
                    CheckPair(layer, camera, crossing - 1, crossing, report);
                    CheckPair(layer, camera, crossing, crossing + 1, report);
                    camera.transform.position = originalCamera + new Vector3(2.5f, 1.25f, 0);
                    CheckPair(layer, camera, crossing - 1, crossing + 1, report);
                    camera.transform.position = originalCamera;
                    CheckPair(layer, camera, crossing - 1, crossing + 1, report, new Vector3(2.5f, 1.25f, 0));
                }
            }
        }

        private static void CheckPair(WzMapLayerBehaviour layer, Camera camera, double before, double after, Report report, Vector3 cameraShift = default)
        {
            Vector3 originalCamera = camera.transform.position;
            layer.Refresh(before);
            Near(layer.transform.position, ExpectedRoot(layer, camera, before, true), "Scroll position before boundary");
            var first = layer.GetComponentsInChildren<WzSpriteAnimator>(false).Select(item => item.transform.position).ToArray();
            Vector3 beforeRoot = ExpectedRoot(layer, camera, before, false);
            camera.transform.position += cameraShift;
            Vector3 delta = ExpectedRoot(layer, camera, after, false) - beforeRoot;
            layer.Refresh(after);
            Near(layer.transform.position, ExpectedRoot(layer, camera, after, true), "Scroll position after boundary");
            var second = layer.GetComponentsInChildren<WzSpriteAnimator>(false).Select(item => item.transform.position).ToArray();
            float halfX = camera.orthographicSize * camera.aspect;
            float halfY = camera.orthographicSize;
            var bg = layer.source.background;
            int checkedCopies = 0;
            foreach (Vector3 point in first)
            {
                // Ignore surplus pooled edge tiles; central copies must remain equivalent even when indices change.
                if (bg.repeatX && Math.Abs(point.x - camera.transform.position.x) > halfX + TileSize(layer, false) / (2 * layer.pixelsPerUnit)) continue;
                if (bg.repeatY && Math.Abs(point.y - camera.transform.position.y) > halfY + TileSize(layer, true) / (2 * layer.pixelsPerUnit)) continue;
                if (!second.Any(candidate => Vector3.Distance(candidate, point + delta) < 0.0002f))
                    throw new InvalidOperationException(layer.source.id + ": visible tile teleported between " + before + " and " + after + " ms; expected continuation " + (point + delta));
                checkedCopies++;
            }
            if (checkedCopies == 0) throw new InvalidOperationException("No core visible copies checked for " + layer.source.id);
            report.checkedPairs++;
            camera.transform.position = originalCamera;
        }

        private static Vector3 ExpectedRoot(WzMapLayerBehaviour layer, Camera camera, double time, bool wrap)
        {
            var bg = layer.source.background;
            double x = layer.source.x; double y = layer.source.y;
            if (bg.scrollX)
            {
                double offset = bg.rx * Math.Max(1, bg.scrollDistanceX) * time / 20000d;
                x += wrap && bg.repeatX ? offset % TileSize(layer, false) : offset;
            }
            else if (bg.parallax) x += camera.transform.position.x * layer.pixelsPerUnit * (1 + bg.rx / 100f);
            if (bg.scrollY)
            {
                double offset = bg.ry * Math.Max(1, bg.scrollDistanceY) * time / 20000d;
                y += wrap && bg.repeatY ? offset % TileSize(layer, true) : offset;
            }
            else if (bg.parallax) y += -camera.transform.position.y * layer.pixelsPerUnit * (1 + bg.ry / 100f);
            return new Vector3((float)Math.Floor((float)x) / layer.pixelsPerUnit, -(float)Math.Floor((float)y) / layer.pixelsPerUnit, 0);
        }

        private static float TileSize(WzMapLayerBehaviour layer, bool vertical)
        {
            float explicitSize = Math.Abs(vertical ? layer.source.background.cy : layer.source.background.cx);
            return Math.Max(1, explicitSize > 0 ? explicitSize : Math.Abs(vertical ? layer.repeatSizePixels.y : layer.repeatSizePixels.x));
        }
        private static void Near(Vector3 actual, Vector3 expected, string message)
        {
            if (Vector3.Distance(actual, expected) > 0.0002f) throw new InvalidOperationException(message + ": expected " + expected + ", got " + actual);
        }
    }
}
