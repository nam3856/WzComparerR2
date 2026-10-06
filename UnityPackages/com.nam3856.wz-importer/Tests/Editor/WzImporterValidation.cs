using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WzUnity;
using Newtonsoft.Json;

namespace WzComparerR2.Unity.Editor
{
    /// <summary>Runs end-to-end fixtures in the actual Editor, including asset persistence and rollback.</summary>
    public static class WzImporterValidation
    {
        [Serializable] public sealed class Report
        {
            public bool passed;
            public string unityVersion;
            public List<string> checks = new List<string>();
            public string error;
        }

        [MenuItem("Tools/WZ Importer/Validate Importer")]
        public static void RunMenu() => Debug.Log(JsonUtility.ToJson(Run(), true));
        public static Report ValidateSynthetic() => Run();

        public static Report Run()
        {
            var report = new Report { unityVersion = Application.unityVersion };
            string fixture = Path.GetFullPath("Library/WzImporterValidation/fixture");
            string output = null;
            var previous = SceneManager.GetActiveScene();
            string priorPath = previous.path;
            bool priorDirty = previous.isDirty;
            Scene validationScene = default;
            try
            {
                var oldEntity = JsonConvert.DeserializeObject<WzEntity>("{\"id\":\"old-avatar\",\"kind\":\"avatar\"}");
                Require(!oldEntity.hasEquipmentMetadata && oldEntity.equipment.Count == 0,
                    "An older manifest invented equipment metadata.");
                report.checks.Add("Older manifests retain unknown outfit presence and an empty equipment inventory");
                Directory.CreateDirectory(fixture);
                var manifest = CreateFixture(fixture);
                string manifestPath = Path.Combine(fixture, "wz-unity.json");
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest));
                if (!AssetDatabase.IsValidFolder(WzUnityImporter.OutputRoot))
                {
                    Directory.CreateDirectory(WzUnityImporter.OutputRoot);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                }
                string plannedOutput = WzUnityImporter.OutputRoot + "/" + WzUnityImporter.StableName(manifest.id);
                Require(!Directory.Exists(plannedOutput), "The isolated validation output already exists.");
                output = plannedOutput;
                string folderGuid = AssetDatabase.CreateFolder(WzUnityImporter.OutputRoot, Path.GetFileName(output));
                Require(!string.IsNullOrEmpty(folderGuid), "Could not create the empty validation output folder.");
                byte[] folderMeta = File.ReadAllBytes(output + ".meta");
                string unmanagedFile = output + "/unmanaged.txt";
                File.WriteAllText(unmanagedFile, "User-owned content must survive a rejected first import.");
                AssetDatabase.ImportAsset(unmanagedFile, ImportAssetOptions.ForceSynchronousImport);
                byte[] unmanagedBytes = File.ReadAllBytes(unmanagedFile);
                byte[] unmanagedMeta = File.ReadAllBytes(unmanagedFile + ".meta");
                var unmanagedEntries = Directory.GetFileSystemEntries(output).OrderBy(path => path, StringComparer.Ordinal).ToArray();
                bool unmanagedRejected = false;
                try { WzUnityImporter.ImportDirectory(fixture); } catch (IOException) { unmanagedRejected = true; }
                Require(unmanagedRejected, "A nonempty unmanaged output folder was accepted.");
                Require(unmanagedBytes.SequenceEqual(File.ReadAllBytes(unmanagedFile)) && unmanagedMeta.SequenceEqual(File.ReadAllBytes(unmanagedFile + ".meta")), "Rejected import changed unmanaged file bytes or metadata.");
                Require(unmanagedEntries.SequenceEqual(Directory.GetFileSystemEntries(output).OrderBy(path => path, StringComparer.Ordinal)), "Rejected import created or removed unmanaged folder contents.");
                Require(folderMeta.SequenceEqual(File.ReadAllBytes(output + ".meta")) && AssetDatabase.AssetPathToGUID(output) == folderGuid, "Rejected import changed the existing folder metadata or GUID.");
                Require(AssetDatabase.DeleteAsset(unmanagedFile), "Could not remove the isolated unmanaged-file fixture.");
                string unmanagedChild = output + "/UnmanagedChild";
                Require(!string.IsNullOrEmpty(AssetDatabase.CreateFolder(output, "UnmanagedChild")), "Could not create the unmanaged child-folder fixture.");
                byte[] childMeta = File.ReadAllBytes(unmanagedChild + ".meta");
                unmanagedRejected = false;
                try { WzUnityImporter.ImportDirectory(fixture); } catch (IOException) { unmanagedRejected = true; }
                Require(unmanagedRejected && Directory.Exists(unmanagedChild) && childMeta.SequenceEqual(File.ReadAllBytes(unmanagedChild + ".meta")), "An unmanaged child folder was accepted or modified.");
                Require(AssetDatabase.DeleteAsset(unmanagedChild), "Could not remove the isolated unmanaged child-folder fixture.");
                Require(!Directory.EnumerateFileSystemEntries(output).Any(), "The first-import fixture is not completely empty.");
                report.checks.Add("Nonempty unmanaged output folders, including an empty child folder, are rejected without modifying their contents or metadata");
                var first = WzUnityImporter.ImportDirectory(fixture);
                Require(first.assetDirectory == output && File.Exists(output + "/wz-import-owner.json") && File.Exists(first.scenePath), "First import into an existing empty folder did not produce managed output and a scene.");
                Require(AssetDatabase.AssetPathToGUID(output) == folderGuid && folderMeta.SequenceEqual(File.ReadAllBytes(output + ".meta")), "First import changed the existing empty folder GUID or metadata.");
                report.checks.Add("First import accepts a completely empty existing folder and preserves its folder GUID and metadata");
                var guids = AssetDatabase.FindAssets("", new[] { output }).ToDictionary(guid => AssetDatabase.GUIDToAssetPath(guid), guid => guid);
                string texturePath = guids.Keys.Single(path => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                DateTime textureWritten = File.GetLastWriteTimeUtc(texturePath);
                File.WriteAllText(output + "/user-notes.txt", "Keep user content");
                var second = WzUnityImporter.ImportDirectory(fixture);
                Require(File.GetLastWriteTimeUtc(texturePath) == textureWritten, "Unchanged validated PNG was rewritten instead of reused.");
                Require(guids.All(pair => AssetDatabase.AssetPathToGUID(pair.Key) == pair.Value), "Reimport changed asset GUIDs.");
                Require(File.ReadAllText(output + "/user-notes.txt") == "Keep user content", "Reimport touched user content.");
                report.checks.Add("Import/reimport keeps asset GUIDs and unrelated user files");
                var textureImporter = (TextureImporter)AssetImporter.GetAtPath(texturePath);
                textureImporter.filterMode = FilterMode.Bilinear;
                textureImporter.SaveAndReimport();
                WzUnityImporter.ImportDirectory(fixture);
                Require(((TextureImporter)AssetImporter.GetAtPath(texturePath)).filterMode == FilterMode.Point, "Changed import settings incorrectly reused.");
                using (var stream = new FileStream(texturePath, FileMode.Append)) stream.WriteByte(0);
                WzUnityImporter.ImportDirectory(fixture);
                Require(File.ReadAllBytes(texturePath).SequenceEqual(File.ReadAllBytes(Path.Combine(fixture, "frame.png"))), "Changed target PNG bytes incorrectly reused.");
                Require(guids.All(pair => AssetDatabase.AssetPathToGUID(pair.Key) == pair.Value), "Repairing PNG/settings changed asset GUIDs.");
                report.checks.Add("PNG reuse requires matching SHA and sprite settings; changed bytes/settings are repaired without GUID changes");

                validationScene = EditorSceneManager.OpenScene(second.scenePath, OpenSceneMode.Additive);
                SceneManager.SetActiveScene(validationScene);
                var sceneRoot = validationScene.GetRootGameObjects().Single(root => root.GetComponent<WzMapScene>() != null);
                Require(sceneRoot.GetComponentsInChildren<EdgeCollider2D>(true).Length == 2, "Foothold/wall colliders missing.");
                Require(sceneRoot.GetComponentsInChildren<WzPortalInfo>(true).Single().source.targetMap == "100000001", "Portal metadata lost.");
                Require(sceneRoot.GetComponentsInChildren<WzLadderInfo>(true).Length == 1, "Ladder missing.");
                var placements = sceneRoot.GetComponentsInChildren<WzPlacementInfo>(true);
                var placement = placements.Single(item => !string.IsNullOrEmpty(item.source.entityId));
                Require(placements.Any(item => string.IsNullOrEmpty(item.source.entityId) && item.GetComponent<WzSpriteAnimator>() == null), "Unsupported placement metadata placeholder missing.");
                Require(PrefabUtility.IsPartOfPrefabInstance(placement.gameObject), "Life placement is not a prefab instance.");
                Require(Vector2.Distance(placement.transform.localPosition, new Vector2(0.2f, -0.4f)) < 0.0001, "Original spawn position changed.");
                report.checks.Add("Saved/reopened map preserves prefab instances, source placement, colliders, ladder and portal metadata");

                string prefabPath = output + "/Prefabs/" + WzUnityImporter.StableName("fixture/mob") + ".prefab";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Require(prefab.GetComponentsInChildren<SpriteRenderer>(true).Length == 2, "Prefab render children were not serialized.");
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, validationScene);
                var animator = instance.GetComponent<WzSpriteAnimator>();
                var importedEquipment = animator.animationSet.equipment;
                Require(animator.animationSet.hasEquipmentMetadata && importedEquipment.Length == 1
                    && importedEquipment[0].slotIndex == 25 && importedEquipment[0].itemId == "1116125"
                    && !importedEquipment[0].visible && !importedEquipment[0].hasImage
                    && importedEquipment[0].isIllusionRing && importedEquipment[0].illusionRingClassificationKnown
                    && importedEquipment[0].metadata.Any(item => item.key == "info/illusionGrade" && item.value == "0"),
                    "Saved/reopened animation asset lost an invisible equipment item or grade-zero ring classification.");
                report.checks.Add("Animation assets retain structured equipment, invisible info-only items and verified grade-zero ring evidence");
                int rendererCount = instance.GetComponentsInChildren<SpriteRenderer>(true).Length;
                animator.Play("stand");
                animator.Advance(0.08f);
                var body = instance.GetComponentsInChildren<WzRenderFragment>(true).Single(fragment => fragment.key.StartsWith("body\n")).GetComponent<SpriteRenderer>();
                var face = instance.GetComponentsInChildren<WzRenderFragment>(true).Single(fragment => fragment.key.StartsWith("face/eyes\n")).GetComponent<SpriteRenderer>();
                Require(Math.Abs(body.transform.localPosition.x - 0.13f) < 0.0001f, "Variable body frame duration/origin incorrect.");
                Require(Math.Abs(face.transform.localPosition.x - 0.34f) < 0.0001f, "Pose clock or independent face frame incorrect.");
                Require(face.enabled, "Absolute pose visibility did not override an invisible base frame.");
                animator.Advance(0.04f);
                Require(Math.Abs(face.transform.localPosition.x - 0.24f) < 0.0001f, "Face track did not loop independently.");
                animator.FlipX = true;
                Require(Math.Abs(body.transform.localPosition.x + 0.13f) < 0.0001f, "Flip did not preserve root anchor.");
                animator.gameObject.SetActive(false);
                animator.gameObject.SetActive(true);
                animator.Play("stand");
                Require(instance.GetComponentsInChildren<SpriteRenderer>(true).Length == rendererCount, "OnEnable duplicated renderer children.");
                report.checks.Add("Prefab children survive reload; independent timing, pose sprite/position, anchor flip and OnEnable reuse pass");
                int completed = 0;
                animator.Completed += _ => completed++;
                animator.Play("die");
                animator.Advance(0.25f);
                animator.Advance(1);
                Require(!animator.IsPlaying && completed == 1, "Non-looping completion was not emitted exactly once.");
                report.checks.Add("Non-looping death stops on the final frame and emits one completion");

                var channel = new WzMotionChannel { kind = "keyframes", cycleMs = 1000, keys = new List<WzMotionKey> { new WzMotionKey { timeMs = 0, value = 2, interpolation = "hold" }, new WzMotionKey { timeMs = 500, value = 9 } } };
                Require(WzMapLayerBehaviour.Evaluate(channel, 250, 0) == 2 && WzMapLayerBehaviour.Evaluate(channel, 500, 0) == 9 && WzMapLayerBehaviour.Evaluate(channel, 750, 0) == 9 && WzMapLayerBehaviour.Evaluate(channel, 1250, 0) == 2, "Motion hold/loop incorrect.");
                report.checks.Add("Authored motion hold and cycle timing pass");
                WzScrollValidation.CheckSynthetic();
                report.checks.Add("Scrolling crosses speed-basis boundaries continuously; signed horizontal/vertical tile wraps, W distances, fallback sizes and moved-camera copies pass");

                EditorSceneManager.CloseScene(validationScene, true);
                validationScene = default;
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                string protectedFile = output + "/Animations/" + WzUnityImporter.StableName("fixture/mob") + ".asset";
                byte[] before = File.ReadAllBytes(protectedFile);
                string beforeGuid = AssetDatabase.AssetPathToGUID(protectedFile);
                try
                {
                    using (var transaction = new WzImportTransaction(output))
                    {
                        File.WriteAllText(protectedFile, "Interrupted write");
                        File.WriteAllText(output + "/interrupted.tmp", "partial");
                        throw new OperationCanceledException();
                    }
                }
                catch (OperationCanceledException) { }
                Require(before.SequenceEqual(File.ReadAllBytes(protectedFile)) && beforeGuid == AssetDatabase.AssetPathToGUID(protectedFile) && !File.Exists(output + "/interrupted.tmp"), "Transaction rollback did not restore the previous output.");
                report.checks.Add("Cancellation rollback restores original bytes, GUIDs and removes partial files");

                manifest.assets[0].file = "../escape.png";
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest));
                bool rejected = false;
                try { WzUnityImporter.ImportDirectory(fixture); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && before.SequenceEqual(File.ReadAllBytes(protectedFile)), "Unsafe source path was accepted or damaged output.");
                manifest.assets[0].file = "frame.png";
                manifest.assets[0].sha256 = new string('0', 64);
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest));
                rejected = false;
                try { WzUnityImporter.ImportDirectory(fixture); } catch (InvalidDataException) { rejected = true; }
                Require(rejected && before.SequenceEqual(File.ReadAllBytes(protectedFile)), "Bad checksum was accepted or damaged output.");
                report.checks.Add("Traversal and checksum failures preserve existing output");
                Require(SceneManager.GetActiveScene().path == priorPath && SceneManager.GetActiveScene().isDirty == priorDirty, "Validation modified the user's active scene.");
                report.checks.Add("User active scene and dirty state preserved");
                report.passed = true;
            }
            catch (Exception exception) { report.error = exception.ToString(); }
            finally
            {
                if (validationScene.IsValid() && validationScene.isLoaded) EditorSceneManager.CloseScene(validationScene, true);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                if (output != null && output.StartsWith(WzUnityImporter.OutputRoot + "/validation_", StringComparison.Ordinal)) AssetDatabase.DeleteAsset(output);
                Directory.CreateDirectory("Library/WzImporterValidation");
                File.WriteAllText("Library/WzImporterValidation/latest.json", JsonUtility.ToJson(report, true));
            }
            return report;
        }

        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private static WzUnityManifest CreateFixture(string directory)
        {
            var texture = new Texture2D(8, 12, TextureFormat.RGBA32, false);
            texture.SetPixels(Enumerable.Repeat(Color.white, 96).ToArray()); texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            File.WriteAllBytes(Path.Combine(directory, "frame.png"), png);
            var manifest = new WzUnityManifest { id = "validation_" + Guid.NewGuid().ToString("N"), kind = "map", sourcePath = "validation/source" };
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) hash = WzBundleValidator.Hex(sha.ComputeHash(png));
            manifest.assets.Add(new WzPngAsset { id = "frame", file = "frame.png", width = 8, height = 12, sha256 = hash });
            var body = new WzUnity.WzAnimationTrack { id = "body", slot = "body", kind = "body" };
            body.frames.Add(new WzSpriteFrame { assetId = "frame", delayMs = 75, originX = 15, originY = -3 });
            body.frames.Add(new WzSpriteFrame { assetId = "frame", delayMs = 125, x = 16, originX = 7, originY = 11 });
            var face = new WzUnity.WzAnimationTrack { id = "face/eyes", slot = "head", kind = "face", poseTrack = "body" };
            face.frames.Add(new WzSpriteFrame { assetId = "frame", delayMs = 50, visible = false });
            face.frames.Add(new WzSpriteFrame { assetId = "frame", delayMs = 50, visible = false });
            for (int bodyIndex = 0; bodyIndex < 2; bodyIndex++)
            for (int faceIndex = 0; faceIndex < 2; faceIndex++)
                face.poses.Add(new WzTrackPose { poseFrame = bodyIndex, frameIndex = faceIndex, overrideSprite = true, assetId = "frame", x = (bodyIndex + faceIndex + 1) * 10, y = -20, z = 1 });
            var entity = new WzEntity { id = "fixture/mob", kind = "mob", defaultAction = "stand" };
            entity.hasEquipmentMetadata = true;
            entity.equipment.Add(new WzEquippedItem
            {
                slotIndex = 25, slot = "Ring1", itemId = "1116125", name = "Original fixture ring",
                sourcePath = "Character\\Ring\\01116125.img", visible = false, hasImage = false,
                isIllusionRing = true, illusionRingClassificationKnown = true,
                illusionRingSourcePath = "Character\\Ring\\01116125.img\\info\\illusionGrade",
                metadata = new List<WzMetadata> { new WzMetadata { key = "info/illusionGrade", value = "0" } }
            });
            entity.clips.Add(new WzAnimationClip { name = "stand", loop = true, durationMs = 200, tracks = new List<WzUnity.WzAnimationTrack> { body, face } });
            entity.clips.Add(new WzAnimationClip { name = "die", loop = false, durationMs = 200, tracks = new List<WzUnity.WzAnimationTrack> { body } });
            manifest.entities.Add(entity);
            manifest.map = new WzMap { id = "validation-map", left = -500, top = -500, width = 1000, height = 1000 };
            manifest.map.layers.Add(new WzMapLayer { id = "tile", group = "tiles", entityId = entity.id, action = "stand" });
            manifest.map.footholds.Add(new WzFoothold { id = 1, x1 = -100, y1 = 0, x2 = 100, y2 = 0, next = 2 });
            manifest.map.footholds.Add(new WzFoothold { id = 2, x1 = 100, y1 = 0, x2 = 100, y2 = -100, prev = 1 });
            manifest.map.ladders.Add(new WzLadder { id = "1", x = 20, y1 = -100, y2 = 0, ladder = true });
            manifest.map.portals.Add(new WzPortal { id = "1", name = "sp", targetMap = "100000001", x = 50, y = 0 });
            manifest.map.placements.Add(new WzPlacement { id = "1", kind = "mob", entityId = entity.id, x = 20, y = 40, originalY = 35, foothold = 1 });
            manifest.map.placements.Add(new WzPlacement { id = "unsupported", kind = "npc", x = 50, y = 30 });
            manifest.warnings.Add(new WzExportWarning { sourcePath = "validation/unsupported", reason = "Synthetic unsupported placement placeholder" });
            return manifest;
        }
    }
}
