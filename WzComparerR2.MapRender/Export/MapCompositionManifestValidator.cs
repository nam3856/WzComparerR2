using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace WzComparerR2.MapRender.Export
{
    public static class MapCompositionManifestValidator
    {
        public const int MaxCompositionDimension = 30000;
        public const int MaxDurationSeconds = 3 * 60 * 60;
        public const int MaxRepeatedInstances = 10000;

        public static void Validate(
            string packageRootDirectory,
            MapCompositionManifest manifest,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            string root = NormalizeRoot(packageRootDirectory);

            Require(string.Equals(manifest.Schema, MapCompositionManifest.SchemaName, StringComparison.Ordinal), "Unknown manifest schema.");
            Require(manifest.Version == MapCompositionManifest.CurrentVersion, "Unsupported manifest version.");
            Require(manifest.Map != null, "map is required.");
            Require(manifest.Timeline != null, "timeline is required.");
            Require(manifest.WorldRect != null, "worldRect is required.");
            Require(manifest.Viewport != null, "viewport is required.");
            Require(manifest.Visibility != null, "visibility is required.");
            Require(manifest.Assets != null, "assets is required.");
            Require(manifest.Layers != null, "layers is required.");
            Require(manifest.OrderIntervals != null, "orderIntervals is required.");
            Require(manifest.ManagedFiles != null, "managedFiles is required.");

            Require(manifest.Map.Id >= 0, "map.id must not be negative.");
            Require(!string.IsNullOrWhiteSpace(manifest.Map.Name), "map.name is required.");
            Require(manifest.Timeline.FrameRate > 0 && manifest.Timeline.FrameRate <= 120, "frameRate must be between 1 and 120.");
            Require(manifest.Timeline.DurationFrames > 0, "durationFrames must be positive.");
            long maxFrames = (long)manifest.Timeline.FrameRate * MaxDurationSeconds;
            Require(manifest.Timeline.DurationFrames <= maxFrames, "The timeline exceeds After Effects' three-hour limit.");
            ValidateRect(manifest.WorldRect, "worldRect");
            Require(manifest.Viewport.Width > 0 && manifest.Viewport.Height > 0, "viewport dimensions must be positive.");
            Require(manifest.Viewport.Width <= MaxCompositionDimension && manifest.Viewport.Height <= MaxCompositionDimension,
                "The viewport exceeds After Effects' 30000 pixel limit.");
            Require(IsFinite(manifest.Viewport.CenterX) && IsFinite(manifest.Viewport.CenterY),
                "viewport center coordinates must be finite.");
            Require(IsFinite(manifest.Viewport.Scale) && manifest.Viewport.Scale > 0f,
                "viewport scale must be finite and positive.");
            ValidateVisibility(manifest.Visibility, cancellationToken);

            var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string relativePath in manifest.ManagedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = NormalizeRelativePath(relativePath);
                Require(managed.Add(normalized), "Duplicate managed file: " + relativePath);
            }

            var assetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exactAssetIds = new HashSet<string>(StringComparer.Ordinal);
            var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MapCompositionAsset asset in manifest.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(asset != null, "assets cannot contain null entries.");
                Require(!string.IsNullOrWhiteSpace(asset.Id), "Every asset requires an id.");
                Require(assetIds.Add(asset.Id), "Duplicate asset id: " + asset.Id);
                exactAssetIds.Add(asset.Id);
                Require(asset.Kind == "png-animation" || asset.Kind == "spine-sequence", "Unsupported asset kind: " + asset.Kind);
                Require(asset.AlphaMode == "straight", "Only straight-alpha package PNGs are supported.");
                Require(asset.Frames != null && asset.Frames.Count > 0, "Asset has no frames: " + asset.Id);
                if (asset.Kind == "spine-sequence")
                {
                    Require(asset.AnimationName != null && asset.SkinName != null,
                        "Spine assets must record animationName and skinName (empty means setup/default): " + asset.Id);
                    Require(asset.SpineVersion == "v2" || asset.SpineVersion == "v4",
                        "Spine assets must declare spineVersion v2 or v4: " + asset.Id);
                    Require(asset.SourcePremultipliedAlpha.HasValue,
                        "Spine assets must record sourcePremultipliedAlpha: " + asset.Id);
                }

                int? spineWidth = null;
                int? spineHeight = null;
                int? spineOriginX = null;
                int? spineOriginY = null;
                int expectedSpineIndex = 0;
                foreach (MapCompositionAssetFrame frame in asset.Frames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Require(frame != null, "Asset frames cannot contain null entries: " + asset.Id);
                    string relativePath = NormalizeRelativePath(frame.Path);
                    Require(managed.Contains(relativePath), "Asset frame is not declared in managedFiles: " + frame.Path);
                    Require(referencedFiles.Add(relativePath), "Duplicate asset frame path: " + frame.Path);
                    Require(string.Equals(Path.GetExtension(relativePath), ".png", StringComparison.OrdinalIgnoreCase),
                        "Asset frame paths must end with .png: " + frame.Path);
                    string fullPath = ResolvePath(root, relativePath);
                    Require(File.Exists(fullPath), "Missing asset frame: " + frame.Path);
                    Require(frame.Width > 0 && frame.Height > 0, "PNG dimensions must be positive: " + frame.Path);
                    Require(frame.Width <= MaxCompositionDimension && frame.Height <= MaxCompositionDimension,
                        "PNG exceeds After Effects' dimension limit: " + frame.Path);
                    Require(frame.DelayMs >= 0, "delayMs must not be negative: " + frame.Path);
                    Require(frame.DurationFrames > 0, "durationFrames must be positive: " + frame.Path);
                    Require(frame.A0 >= 0 && frame.A0 <= 255 && frame.A1 >= 0 && frame.A1 <= 255,
                        "Frame alpha must be between 0 and 255: " + frame.Path);

                    using (Image image = Image.FromFile(fullPath))
                    {
                        Require(image.Width == frame.Width && image.Height == frame.Height,
                            "PNG dimensions do not match the manifest: " + frame.Path);
                    }
                    Require(IsLowercaseSha256(frame.Sha256), "sha256 must be 64 lowercase hexadecimal characters: " + frame.Path);
                    Require(IsBlendMode(asset.BlendMode), "Unsupported asset blend mode: " + asset.Id);
                    Require(IsBlendMode(frame.BlendMode), "Unsupported frame blend mode: " + frame.Path);
                    string actualHash = ComputeSha256(fullPath);
                    Require(string.Equals(actualHash, frame.Sha256, StringComparison.Ordinal),
                        "PNG hash does not match the manifest: " + frame.Path);

                    if (asset.Kind == "spine-sequence")
                    {
                        int index = ParseSequenceIndex(relativePath);
                        Require(index == expectedSpineIndex++, "Spine frame sequence is not contiguous: " + frame.Path);
                        spineWidth = spineWidth ?? frame.Width;
                        spineHeight = spineHeight ?? frame.Height;
                        spineOriginX = spineOriginX ?? frame.OriginX;
                        spineOriginY = spineOriginY ?? frame.OriginY;
                        Require(spineWidth == frame.Width && spineHeight == frame.Height
                            && spineOriginX == frame.OriginX && spineOriginY == frame.OriginY,
                            "Spine frames must share union dimensions and origin: " + asset.Id);
                    }
                }
            }

            Require(managed.SetEquals(referencedFiles), "managedFiles must exactly match all asset frame paths.");

            var layerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MapCompositionLayer layer in manifest.Layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(layer != null, "layers cannot contain null entries.");
                Require(!string.IsNullOrWhiteSpace(layer.Id) && layerIds.Add(layer.Id), "Duplicate or empty layer id: " + layer?.Id);
                Require(!string.IsNullOrWhiteSpace(layer.Name), "Layer name is required: " + layer.Id);
                Require(layer.SemanticLayer == "Back" || layer.SemanticLayer == "Tile"
                    || layer.SemanticLayer == "Obj" || layer.SemanticLayer == "Front",
                    "Unsupported semanticLayer: " + layer.Id);
                Require(exactAssetIds.Contains(layer.AssetId), "Layer references an unknown asset or uses mismatched casing: " + layer.Id);
                Require(layer.DrawOrder >= 0, "drawOrder must not be negative: " + layer.Id);
                Require(layer.StartFrame >= 0 && layer.EndFrame > layer.StartFrame
                    && layer.EndFrame <= manifest.Timeline.DurationFrames, "Invalid layer frame range: " + layer.Id);
                Require(IsFinite(layer.PositionX) && IsFinite(layer.PositionY) && layer.SourceTimeOffsetMs >= 0,
                    "Layer position must be finite and sourceTimeOffsetMs must not be negative: " + layer.Id);
                Require(IsBlendMode(layer.BlendMode), "Unsupported layer blend mode: " + layer.Id);
                if (layer.TileWidth.HasValue) Require(layer.TileWidth.Value >= 0, "tileWidth must not be negative: " + layer.Id);
                if (layer.TileHeight.HasValue) Require(layer.TileHeight.Value >= 0, "tileHeight must not be negative: " + layer.Id);
                Require(layer.TransformKeys != null && layer.TransformKeys.Count > 0, "Layer requires at least one transform key: " + layer.Id);
                int previousFrame = -1;
                foreach (MapCompositionTransformKey key in layer.TransformKeys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Require(key.Frame > previousFrame && key.Frame >= layer.StartFrame && key.Frame < layer.EndFrame,
                        "Transform keys must be strictly increasing and inside the layer range: " + layer.Id);
                    Require(IsFinite(key.X) && IsFinite(key.Y) && IsFinite(key.ScaleX) && IsFinite(key.ScaleY)
                        && IsFinite(key.Rotation) && IsFinite(key.Opacity), "Transform values must be finite: " + layer.Id);
                    Require(key.Opacity >= 0f && key.Opacity <= 100f, "Transform opacity must be between 0 and 100: " + layer.Id);
                    previousFrame = key.Frame;
                }
                if (layer.Repeat != null)
                {
                    Require(layer.Repeat.CountX > 0 && layer.Repeat.CountY > 0, "Repeat counts must be positive: " + layer.Id);
                    Require((long)layer.Repeat.CountX * layer.Repeat.CountY <= MaxRepeatedInstances,
                        "Repeat expansion exceeds 10000 instances: " + layer.Id);
                    Require(layer.Repeat.CountX == 1 || layer.Repeat.StepX != 0, "Repeated X layers require a non-zero step: " + layer.Id);
                    Require(layer.Repeat.CountY == 1 || layer.Repeat.StepY != 0, "Repeated Y layers require a non-zero step: " + layer.Id);
                }
            }

            foreach (MapCompositionUnsupportedItem item in manifest.Unsupported ?? Enumerable.Empty<MapCompositionUnsupportedItem>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(item != null && !string.IsNullOrWhiteSpace(item.Path)
                    && !string.IsNullOrWhiteSpace(item.Kind) && !string.IsNullOrWhiteSpace(item.Reason),
                    "Every unsupported item must include path, kind, and reason.");
            }

            ValidateOrderIntervals(manifest, layerIds, cancellationToken);
        }

        public static string NormalizeRelativePath(string path)
        {
            Require(!string.IsNullOrWhiteSpace(path), "Asset paths cannot be empty.");
            Require(!Path.IsPathRooted(path), "Asset paths must be relative: " + path);
            Require(path.IndexOf('\\') < 0 && path.IndexOf(':') < 0 && !path.StartsWith("/", StringComparison.Ordinal),
                "Asset paths must use forward-slash package-relative syntax: " + path);
            string normalized = path;
            string[] parts = normalized.Split('/');
            Require(parts.All(part => !string.IsNullOrWhiteSpace(part) && part != "." && part != ".."), "Unsafe asset path: " + path);
            return normalized;
        }

        internal static string ResolvePath(string normalizedRoot, string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            string fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            Require(fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase), "Asset path leaves the package: " + relativePath);
            return fullPath;
        }

        internal static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static void ValidateRect(MapCompositionRect rect, string name)
        {
            Require(rect.Width > 0 && rect.Height > 0, name + " dimensions must be positive.");
            Require(rect.Width <= MaxCompositionDimension && rect.Height <= MaxCompositionDimension,
                name + " exceeds After Effects' 30000 pixel limit.");
        }

        private static void ValidateVisibility(
            MapCompositionVisibility visibility,
            CancellationToken cancellationToken)
        {
            Require(visibility.Categories != null, "visibility.categories is required.");
            Require(visibility.Tags != null, "visibility.tags is required.");
            Require(visibility.Quests != null, "visibility.quests is required.");
            Require(visibility.QuestEx != null, "visibility.questEx is required.");
            foreach (string key in visibility.Categories.Keys.Concat(visibility.Tags.Keys))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(!string.IsNullOrWhiteSpace(key), "Visibility category and tag keys cannot be empty.");
            }
            foreach (string questId in visibility.Quests.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(int.TryParse(questId, NumberStyles.None, CultureInfo.InvariantCulture, out _),
                    "visibility.quests keys must be invariant decimal quest IDs: " + questId);
            }
            foreach (string questExKey in visibility.QuestEx.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int separator = questExKey?.IndexOf(':') ?? -1;
                Require(separator > 0
                    && int.TryParse(questExKey.Substring(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out _),
                    "visibility.questEx keys must use '<questId>:<qkey>': " + questExKey);
            }
        }

        private static bool IsBlendMode(string value)
        {
            return value == "normal" || value == "additive" || value == "screen" || value == "multiply";
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsLowercaseSha256(string value)
        {
            if (value == null || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static void ValidateOrderIntervals(
            MapCompositionManifest manifest,
            HashSet<string> layerIds,
            CancellationToken cancellationToken)
        {
            Require(manifest.OrderIntervals.Count > 0, "At least one order interval is required.");
            int nextStart = 0;
            foreach (MapCompositionOrderInterval interval in manifest.OrderIntervals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Require(interval != null, "orderIntervals cannot contain null entries.");
                Require(interval.StartFrame == nextStart && interval.EndFrame > interval.StartFrame,
                    "Order intervals must be contiguous, non-empty, and begin at frame zero.");
                Require(interval.EndFrame <= manifest.Timeline.DurationFrames, "Order interval exceeds the timeline.");
                Require(interval.LayerIds != null, "Order interval layerIds is required.");
                var intervalIds = new HashSet<string>(interval.LayerIds, StringComparer.Ordinal);
                Require(intervalIds.Count == interval.LayerIds.Count && intervalIds.SetEquals(layerIds),
                    "Every order interval must contain every layer exactly once.");
                nextStart = interval.EndFrame;
            }
            Require(nextStart == manifest.Timeline.DurationFrames, "Order intervals must cover the complete timeline.");
        }

        private static int ParseSequenceIndex(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            Match match = Regex.Match(name ?? string.Empty, @"(\d+)$", RegexOptions.CultureInvariant);
            Require(match.Success, "Spine sequence filenames must end in a numeric index: " + path);
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static string NormalizeRoot(string packageRootDirectory)
        {
            if (packageRootDirectory == null) throw new ArgumentNullException(nameof(packageRootDirectory));
            string root = Path.GetFullPath(packageRootDirectory);
            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
    }
}
