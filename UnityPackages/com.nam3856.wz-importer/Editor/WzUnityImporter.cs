using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using WzUnity;

namespace WzComparerR2.Unity.Editor
{
    [Serializable]
    public sealed class WzImportResult
    {
        public string bundleId;
        public string assetDirectory;
        public string scenePath;
        public int textures;
        public int prefabs;
        public int warningCount;
        public string status;
    }

    [Serializable]
    internal sealed class WzOwnership
    {
        public string importer = "com.nam3856.wz-importer";
        public string bundleId;
        public List<string> files = new List<string>();
    }

    public static class WzUnityImporter
    {
        public const string OutputRoot = "Assets/MapleImported";
        private const string MarkerName = "wz-import-owner.json";

        [MenuItem("Tools/WZ Importer/Import Export Folder...")]
        public static void ImportMenu()
        {
            string folder = EditorUtility.OpenFolderPanel("Select folder containing wz-unity.json", "", "");
            if (string.IsNullOrEmpty(folder)) return;
            try
            {
                var result = ImportDirectory(folder, true);
                Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(result.scenePath ?? result.assetDirectory);
                EditorUtility.DisplayDialog("WZ import complete", result.prefabs + " prefabs, " + result.textures + " sprites.\n" + result.assetDirectory + (result.warningCount > 0 ? "\nWarnings: " + result.warningCount + ". See wz-unity.json for source paths and reasons." : ""), "OK");
            }
            catch (OperationCanceledException) { Debug.Log("WZ import cancelled. Previous output was restored."); }
            catch (Exception exception) { Debug.LogException(exception); EditorUtility.DisplayDialog("WZ import failed", exception.Message + "\nPrevious output was preserved.", "OK"); }
        }

        /// <summary>Validates the entire bundle first. Only this bundle's managed files can be replaced.</summary>
        public static WzImportResult ImportDirectory(string sourceDirectory, bool interactive = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before importing WZ assets.");
            var manifest = WzBundleValidator.ReadAndValidate(sourceDirectory);
            var output = OutputRoot + "/" + StableName(manifest.id);
            var expected = PlannedFiles(manifest, output);
            var owner = ReadOwnership(output, manifest.id);
            ValidateOwnership(output, owner, expected);
            var scenePath = manifest.map == null ? null : output + "/Scenes/" + StableName(manifest.map.id) + ".unity";
            Scene priorScene = SceneManager.GetActiveScene();
            bool reopenScene = false;
            bool restoreImportedSceneAsActive = scenePath != null && priorScene.path == scenePath;
            Scene holdingScene = default;
            if (scenePath != null)
            {
                var loaded = SceneManager.GetSceneByPath(scenePath);
                if (loaded.IsValid() && loaded.isLoaded)
                {
                    if (loaded.isDirty) throw new InvalidOperationException("Save or discard edits in the generated map scene before reimporting: " + scenePath);
                    reopenScene = true;
                    if (SceneManager.sceneCount == 1) holdingScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    EditorSceneManager.CloseScene(loaded, true);
                }
            }
            var result = new WzImportResult { bundleId = manifest.id, assetDirectory = output, scenePath = scenePath, textures = manifest.assets.Count, prefabs = manifest.entities.Count, warningCount = manifest.warnings == null ? 0 : manifest.warnings.Count };
            try
            {
            using (var transaction = new WzImportTransaction(output))
            {
                Scene staging = default;
                try
                {
                    Directory.CreateDirectory(output);
                    foreach (var folder in new[] { "Textures", "Animations", "Prefabs", "Materials", "Scenes" }) Directory.CreateDirectory(output + "/" + folder);
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    var sprites = ImportSprites(manifest, sourceDirectory, output, interactive);
                    var material = ImportMaterial(output + "/Materials/WzSpriteUnlit.mat");
                    var prefabs = new Dictionary<string, GameObject>(StringComparer.Ordinal);
                    staging = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    SceneManager.SetActiveScene(staging);
                    for (int index = 0; index < manifest.entities.Count; index++)
                    {
                        var entity = manifest.entities[index];
                        Progress(interactive, "Creating prefab " + entity.id, 0.4f + 0.4f * index / manifest.entities.Count);
                        var set = ConvertAnimation(manifest, entity, sprites);
                        string setPath = output + "/Animations/" + StableName(entity.id) + ".asset";
                        set = SaveAsset(set, setPath);
                        var root = new GameObject(string.IsNullOrEmpty(entity.displayName) ? entity.id : entity.displayName);
                        try
                        {
                            var animator = root.AddComponent<WzSpriteAnimator>();
                            animator.animationSet = set;
                            animator.spriteMaterial = material;
                            animator.Play(set.defaultAction);
                            string prefabPath = output + "/Prefabs/" + StableName(entity.id) + ".prefab";
                            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
                            if (!success || prefab == null) throw new IOException("Could not save prefab " + entity.id);
                            prefabs.Add(entity.id, prefab);
                        }
                        finally { UnityEngine.Object.DestroyImmediate(root); }
                    }
                    EditorSceneManager.CloseScene(staging, true);
                    staging = default;
                    if (priorScene.IsValid() && priorScene.isLoaded) SceneManager.SetActiveScene(priorScene);
                    if (manifest.map != null)
                    {
                        Progress(interactive, "Creating map scene", 0.85f);
                        WzSceneBuilder.Build(manifest, prefabs, scenePath);
                    }
                    Progress(interactive, "Saving imported assets", 0.95f);
                    File.Copy(Path.Combine(sourceDirectory, "wz-unity.json"), output + "/wz-unity.json", true);
                    foreach (var stale in owner.files.Except(expected, StringComparer.OrdinalIgnoreCase))
                    {
                        var fullPath = WzBundleValidator.ResolveInside(output, stale);
                        if (File.Exists(fullPath)) AssetDatabase.DeleteAsset(output + "/" + stale);
                    }
                    File.WriteAllText(output + "/" + MarkerName, JsonUtility.ToJson(new WzOwnership { bundleId = manifest.id, files = expected }, true), new UTF8Encoding(false));
                    result.status = result.warningCount == 0 ? "complete" : "complete_with_warnings";
                    File.WriteAllText(output + "/import-result.json", JsonUtility.ToJson(result, true), new UTF8Encoding(false));
                    foreach (string generatedPath in expected)
                    {
                        var generatedAsset = AssetDatabase.LoadMainAssetAtPath(output + "/" + generatedPath);
                        if (generatedAsset != null) AssetDatabase.SaveAssetIfDirty(generatedAsset);
                    }
                    AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                    transaction.Commit();
                }
                finally
                {
                    if (staging.IsValid() && staging.isLoaded) EditorSceneManager.CloseScene(staging, true);
                    if (priorScene.IsValid() && priorScene.isLoaded) SceneManager.SetActiveScene(priorScene);
                    if (interactive) EditorUtility.ClearProgressBar();
                }
            }
            }
            finally
            {
                if (reopenScene && File.Exists(scenePath))
                {
                    var restored = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                    if (restoreImportedSceneAsActive) SceneManager.SetActiveScene(restored);
                }
                if (holdingScene.IsValid() && holdingScene.isLoaded && SceneManager.sceneCount > 1) EditorSceneManager.CloseScene(holdingScene, true);
            }
            Debug.Log("WZ import " + result.status + ": " + result.assetDirectory + " (" + result.prefabs + " prefabs, " + result.warningCount + " warnings)");
            return result;
        }

        private static Dictionary<string, Sprite> ImportSprites(WzUnityManifest manifest, string source, string output, bool interactive)
        {
            var sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            var paths = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            for (int index = 0; index < manifest.assets.Count; index++)
            {
                var asset = manifest.assets[index];
                Progress(interactive, "Importing PNG " + (index + 1) + "/" + manifest.assets.Count, 0.4f * index / Math.Max(1, manifest.assets.Count));
                string target = output + "/Textures/" + TextureName(asset) + ".png";
                if (paths.TryGetValue(target, out var reused)) { sprites.Add(asset.id, reused); continue; }
                if (TryReuseSprite(target, asset, manifest.pixelsPerUnit, out reused))
                {
                    paths.Add(target, reused);
                    sprites.Add(asset.id, reused);
                    continue;
                }
                File.Copy(WzBundleValidator.ResolveInside(source, asset.file), target, true);
                AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                var importer = AssetImporter.GetAtPath(target) as TextureImporter;
                if (importer == null) throw new InvalidDataException("Unity could not decode PNG: " + asset.file);
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = manifest.pixelsPerUnit;
                importer.spritePivot = new Vector2(0.5f, 0.5f);
                importer.filterMode = FilterMode.Point;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(32, Mathf.Max(asset.width, asset.height)));
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                settings.spriteAlignment = (int)SpriteAlignment.Center;
                importer.SetTextureSettings(settings);
                importer.SaveAndReimport();
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(target);
                if (sprite == null) throw new InvalidDataException("Unity did not create a sprite for " + asset.file);
                paths.Add(target, sprite);
                sprites.Add(asset.id, sprite);
            }
            return sprites;
        }

        private static bool TryReuseSprite(string target, WzPngAsset asset, float pixelsPerUnit, out Sprite sprite)
        {
            sprite = null;
            if (string.IsNullOrEmpty(asset.sha256) || !File.Exists(target)) return false;
            using (var stream = File.OpenRead(target))
            using (var hash = SHA256.Create())
                if (!string.Equals(WzBundleValidator.Hex(hash.ComputeHash(stream)), asset.sha256, StringComparison.OrdinalIgnoreCase)) return false;
            var importer = AssetImporter.GetAtPath(target) as TextureImporter;
            if (importer == null || importer.textureType != TextureImporterType.Sprite || importer.spriteImportMode != SpriteImportMode.Single ||
                importer.spritePixelsPerUnit != pixelsPerUnit || importer.filterMode != FilterMode.Point || importer.mipmapEnabled ||
                !importer.alphaIsTransparency || importer.textureCompression != TextureImporterCompression.Uncompressed || importer.npotScale != TextureImporterNPOTScale.None ||
                importer.maxTextureSize != Mathf.NextPowerOfTwo(Mathf.Max(32, Mathf.Max(asset.width, asset.height)))) return false;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            if (settings.spriteMeshType != SpriteMeshType.FullRect || settings.spriteAlignment != (int)SpriteAlignment.Center || importer.spritePivot != new Vector2(0.5f, 0.5f)) return false;
            sprite = AssetDatabase.LoadAssetAtPath<Sprite>(target);
            return sprite != null && sprite.rect.width == asset.width && sprite.rect.height == asset.height;
        }

        private static Material ImportMaterial(string path)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("A sprite Unlit shader is required.");
            if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, path); }
            else { material.shader = shader; EditorUtility.SetDirty(material); }
            return material;
        }

        private static WzAnimationSet ConvertAnimation(WzUnityManifest manifest, WzEntity entity, Dictionary<string, Sprite> sprites)
        {
            float ppu = manifest.pixelsPerUnit;
            var set = ScriptableObject.CreateInstance<WzAnimationSet>();
            set.sourceId = entity.id;
            set.sourcePath = entity.sourcePath;
            set.defaultAction = entity.defaultAction;
            set.pixelsPerUnit = ppu;
            set.metadata = (entity.metadata ?? new List<WzMetadata>()).ToArray();
            set.actions = entity.clips.Select(clip => new WzAnimationAction
            {
                name = clip.name, loop = clip.loop, durationMs = clip.durationMs,
                tracks = clip.tracks.Select(track => new WzAnimationTrack
                {
                    name = track.id, loop = track.loop, startMs = track.startMs, poseTrack = track.poseTrack,
                    metadata = (track.metadata ?? new List<WzMetadata>()).Concat(new[] { new WzMetadata { key = "kind", value = track.kind }, new WzMetadata { key = "itemId", value = track.itemId } }).ToArray(),
                    frames = track.frames.Select(frame =>
                    {
                        var sprite = string.IsNullOrEmpty(frame.assetId) ? null : sprites[frame.assetId];
                        var extent = sprite == null ? Vector2.zero : sprite.rect.size / (2 * ppu);
                        return new WzAnimationFrame
                        {
                            durationMs = frame.delayMs,
                            slices = new[] { new WzRenderSlice
                            {
                                slot = string.IsNullOrEmpty(track.slot) ? track.kind ?? "body" : track.slot,
                                part = track.id, sprite = sprite,
                                anchor = WzSceneBuilder.Point(frame.x, frame.y, ppu),
                                origin = WzSceneBuilder.Point(frame.originX, frame.originY, ppu),
                                position = WzSceneBuilder.Point(frame.x - frame.originX, frame.y - frame.originY, ppu) + new Vector2(extent.x, -extent.y),
                                scale = new Vector2(frame.scaleX, frame.scaleY), rotation = -frame.rotation,
                                sourceZ = frame.z, sortingOrder = frame.drawOrder,
                                alpha = Mathf.Clamp01(frame.a0 / 255f), endAlpha = Mathf.Clamp01(frame.a1 / 255f), visible = frame.visible
                            } }
                        };
                    }).ToArray(),
                    poses = (track.poses ?? new List<WzTrackPose>()).Select(pose => new WzAnimationPose
                    {
                        poseFrame = pose.poseFrame, frameIndex = pose.frameIndex,
                        anchor = WzSceneBuilder.Point(pose.x, pose.y, ppu), origin = WzSceneBuilder.Point(pose.originX, pose.originY, ppu),
                        sourceZ = pose.z, sortingOrder = pose.drawOrder, visible = pose.visible,
                        overrideSprite = pose.overrideSprite,
                        sprite = pose.overrideSprite && !string.IsNullOrEmpty(pose.assetId) ? sprites[pose.assetId] : null
                    }).ToArray()
                }).ToArray()
            }).ToArray();
            return set;
        }

        private static T SaveAsset<T>(T value, string path) where T : UnityEngine.Object
        {
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null) { AssetDatabase.CreateAsset(value, path); return value; }
            EditorUtility.CopySerialized(value, existing);
            UnityEngine.Object.DestroyImmediate(value);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        private static WzOwnership ReadOwnership(string output, string id)
        {
            string marker = output + "/" + MarkerName;
            // Unity may already have created the destination folder and its sibling .meta.
            // Claim it only when it contains no files or subfolders; keep its existing GUID.
            if (!Directory.Exists(output) || !Directory.EnumerateFileSystemEntries(output).Any())
                return new WzOwnership { bundleId = id };
            if (!File.Exists(marker)) throw new IOException("The output folder is not owned by this importer: " + output);
            var owner = JsonUtility.FromJson<WzOwnership>(File.ReadAllText(marker));
            if (owner == null || owner.importer != "com.nam3856.wz-importer" || owner.bundleId != id || owner.files == null) throw new IOException("The import ownership marker does not match this bundle.");
            foreach (var file in owner.files) WzBundleValidator.ResolveInside(output, file);
            return owner;
        }

        private static void ValidateOwnership(string output, WzOwnership owner, List<string> planned)
        {
            var owned = new HashSet<string>(owner.files, StringComparer.OrdinalIgnoreCase);
            foreach (var path in planned)
                if (File.Exists(output + "/" + path) && !owned.Contains(path)) throw new IOException("Refusing to overwrite a file not recorded as generated: " + output + "/" + path);
        }

        private static List<string> PlannedFiles(WzUnityManifest manifest, string output)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Materials/WzSpriteUnlit.mat", "wz-unity.json", "import-result.json" };
            foreach (var asset in manifest.assets) files.Add("Textures/" + TextureName(asset) + ".png");
            foreach (var entity in manifest.entities) { files.Add("Animations/" + StableName(entity.id) + ".asset"); files.Add("Prefabs/" + StableName(entity.id) + ".prefab"); }
            if (manifest.map != null) files.Add("Scenes/" + StableName(manifest.map.id) + ".unity");
            return files.OrderBy(path => path, StringComparer.Ordinal).ToList();
        }

        private static string TextureName(WzPngAsset asset) => !string.IsNullOrEmpty(asset.sha256) ? asset.sha256.ToLowerInvariant() : StableName(asset.id);
        public static string StableName(string id)
        {
            id = id ?? "unnamed";
            string prefix = new string(id.Select(character => char.IsLetterOrDigit(character) || character == '-' || character == '_' ? character : '_').Take(60).ToArray());
            if (prefix.Length == 0) prefix = "asset";
            using (var hash = SHA256.Create()) return prefix + "_" + WzBundleValidator.Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(id))).Substring(0, 12);
        }
        private static void Progress(bool interactive, string text, float value)
        {
            if (interactive && EditorUtility.DisplayCancelableProgressBar("WZ Unity Import", text, value)) throw new OperationCanceledException();
        }
    }
}
