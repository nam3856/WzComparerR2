using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Xna.Framework;
using WzComparerR2.Animation;
using WzComparerR2.Controls;
using WzComparerR2.MapRender.Patches2;

namespace WzComparerR2.MapRender.Export
{
    public sealed class MapCompositionExporter
    {
        public const int DefaultFrameRate = 30;
        public const double DefaultDurationSeconds = 10d;

        private readonly List<FrameAssetCacheEntry> frameAssetCache = new List<FrameAssetCacheEntry>();
        private readonly List<SpineAssetCacheEntry> spineAssetCache = new List<SpineAssetCacheEntry>();
        private readonly HashSet<string> managedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int nextAssetId;

        public MapCompositionExportResult Export(
            MapData mapData,
            string outputDirectory,
            MapCompositionExportOptions options,
            CancellationToken cancellationToken = default(CancellationToken),
            IProgress<MapCompositionExportProgress> progress = null)
        {
            if (mapData == null) throw new ArgumentNullException(nameof(mapData));
            if (string.IsNullOrWhiteSpace(outputDirectory)) throw new ArgumentException("An output directory is required.", nameof(outputDirectory));
            options = options ?? new MapCompositionExportOptions();

            Rectangle worldRect = options.WorldRect ?? mapData.VRect;
            int durationFrames = ValidateOptions(options, worldRect);
            string outputFullPath = Path.GetFullPath(outputDirectory);
            string parentDirectory = Directory.GetParent(outputFullPath)?.FullName;
            string directoryName = new DirectoryInfo(outputFullPath).Name;
            if (string.IsNullOrEmpty(parentDirectory) || string.IsNullOrEmpty(directoryName))
            {
                throw new ArgumentException("The package output must be a named directory below an existing parent.", nameof(outputDirectory));
            }
            Directory.CreateDirectory(parentDirectory);
            string stageDirectory = Path.Combine(parentDirectory, "." + directoryName + ".map-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageDirectory);

            ResetState();
            Exception failure = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new MapCompositionExportProgress("snapshot", 0, 1));
                MapCompositionManifest manifest = CreateManifest(mapData, stageDirectory, options, worldRect, durationFrames,
                    cancellationToken, progress);

                manifest.ManagedFiles = managedFiles.OrderBy(path => path, StringComparer.Ordinal).ToList();
                manifest.Assets.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                manifest.Layers.Sort((left, right) => left.DrawOrder.CompareTo(right.DrawOrder));

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new MapCompositionExportProgress("validate", 0, 1));
                MapCompositionManifestValidator.Validate(stageDirectory, manifest, cancellationToken);
                string stageManifestPath = Path.Combine(stageDirectory, "wz-map.json");
                MapCompositionManifestSerializer.Write(stageManifestPath, manifest);
                MapCompositionManifest roundTrip = MapCompositionManifestSerializer.Read(stageManifestPath);
                MapCompositionManifestValidator.Validate(stageDirectory, roundTrip, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new MapCompositionExportProgress("commit", 0, 1));
                MapCompositionPackageCommitter.Commit(stageDirectory, outputFullPath);

                progress?.Report(new MapCompositionExportProgress("complete", 1, 1));
                return new MapCompositionExportResult
                {
                    OutputDirectory = outputFullPath,
                    ManifestPath = Path.Combine(outputFullPath, "wz-map.json"),
                    Manifest = manifest
                };
            }
            catch (Exception ex)
            {
                failure = ex;
                throw;
            }
            finally
            {
                if (Directory.Exists(stageDirectory))
                {
                    try
                    {
                        Directory.Delete(stageDirectory, true);
                    }
                    catch
                    {
                        if (failure == null) throw;
                    }
                }
            }
        }

        private MapCompositionManifest CreateManifest(
            MapData mapData,
            string stageDirectory,
            MapCompositionExportOptions options,
            Rectangle worldRect,
            int durationFrames,
            CancellationToken cancellationToken,
            IProgress<MapCompositionExportProgress> progress)
        {
            float cameraCenterX = options.CameraCenterX ?? worldRect.Center.X;
            float cameraCenterY = options.CameraCenterY ?? worldRect.Center.Y;

            string sourceName = mapData.Name ?? string.Empty;
            string displayName = options.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = Path.GetFileNameWithoutExtension(sourceName);
            }

            var manifest = new MapCompositionManifest
            {
                Map = new MapCompositionMapInfo
                {
                    Id = mapData.ID ?? 0,
                    Name = displayName ?? string.Empty,
                    LinkedMapId = mapData.Link,
                    Source = sourceName
                },
                Timeline = new MapCompositionTimeline
                {
                    FrameRate = options.FrameRate,
                    DurationFrames = durationFrames
                },
                WorldRect = ToManifestRect(worldRect),
                Viewport = new MapCompositionViewport
                {
                    Width = options.ViewportWidth,
                    Height = options.ViewportHeight,
                    CenterX = cameraCenterX,
                    CenterY = cameraCenterY,
                    Scale = options.CameraScale
                },
                Visibility = CreateVisibilitySnapshot(options)
            };

            var descriptors = CreateLayerDescriptors(mapData, options, manifest);
            int itemCount = descriptors.Count;
            for (int i = 0; i < descriptors.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LayerDescriptor descriptor = descriptors[i];
                progress?.Report(new MapCompositionExportProgress("assets", i, itemCount));
                MapCompositionAsset asset = GetOrCreateAsset(descriptor, stageDirectory, options, durationFrames,
                    manifest, cancellationToken);
                if (asset == null) continue;

                descriptor.Layer.AssetId = asset.Id;
                PopulateLayerTimeline(descriptor, options, worldRect, durationFrames, cancellationToken);
                manifest.Layers.Add(descriptor.Layer);
            }
            progress?.Report(new MapCompositionExportProgress("assets", itemCount, itemCount));

            BuildOrderIntervals(descriptors.Where(descriptor => descriptor.Layer.AssetId != null).ToList(),
                options, durationFrames, manifest, cancellationToken);
            AddUnsupportedSceneItems(mapData, manifest);
            return manifest;
        }

        private List<LayerDescriptor> CreateLayerDescriptors(
            MapData mapData,
            MapCompositionExportOptions options,
            MapCompositionManifest manifest)
        {
            var descriptors = new List<LayerDescriptor>();
            int containerOrder = 0;
            AddBackContainer(mapData.Scene.Back, false, -1, containerOrder++, options, manifest, descriptors);
            for (int layerIndex = 0; layerIndex < mapData.Scene.Layers.Nodes.Count; layerIndex++)
            {
                var layerNode = mapData.Scene.Layers.Nodes[layerIndex] as LayerNode;
                if (layerNode == null) continue;
                AddObjContainer(layerNode.Obj, layerIndex, containerOrder++, options, manifest, descriptors);
                AddTileContainer(layerNode.Tile, layerIndex, containerOrder++, options, manifest, descriptors);
            }
            AddBackContainer(mapData.Scene.Front, true, -1, containerOrder, options, manifest, descriptors);
            return descriptors;
        }

        private void AddBackContainer(
            ContainerNode container,
            bool front,
            int sourceLayer,
            int containerOrder,
            MapCompositionExportOptions options,
            MapCompositionManifest manifest,
            List<LayerDescriptor> descriptors)
        {
            foreach (BackItem item in container.Slots.OfType<BackItem>())
            {
                string sourcePath = GetBackSourcePath(item);
                if (!IsVisible(item, options, front)) continue;
                if (item.View?.Animator == null)
                {
                    AddUnsupported(manifest, sourcePath, "missing-resource", "The visible Back resource has no loaded animator.");
                    continue;
                }
                if (!(item.View.Animator is FrameAnimator) && !(item.View.Animator is ISpineAnimator))
                {
                    AddUnsupported(manifest, sourcePath, "custom-shader", "Custom sprite/shader Back resources are not exported in v1.");
                    continue;
                }

                string semantic = front ? "Front" : "Back";
                var layer = new MapCompositionLayer
                {
                    Id = (front ? "front-" : "back-") + item.Index.ToString("D4", CultureInfo.InvariantCulture),
                    Name = string.IsNullOrWhiteSpace(item.Name) ? sourcePath : item.Name,
                    SemanticLayer = semantic,
                    StartFrame = 0,
                    SourcePath = sourcePath,
                    SourceLayer = sourceLayer,
                    SourceZ = 0,
                    SourceIndex = item.Index,
                    SourceTimeOffsetMs = GetLayerSourceTimeOffset(item.View.Animator),
                    FlipX = item.Flip,
                    TileMode = item.TileMode.ToString(),
                    Rx = item.Rx,
                    Ry = item.Ry,
                    ScreenMode = item.ScreenMode
                };
                descriptors.Add(new LayerDescriptor(layer, item.View.Animator, containerOrder, item.Index, item));
            }
        }

        private void AddObjContainer(
            ContainerNode container,
            int sourceLayer,
            int containerOrder,
            MapCompositionExportOptions options,
            MapCompositionManifest manifest,
            List<LayerDescriptor> descriptors)
        {
            foreach (ObjItem item in container.Slots.OfType<ObjItem>())
            {
                string sourcePath = GetObjSourcePath(item);
                if (item.Light)
                {
                    AddUnsupported(manifest, sourcePath, "light", "Light objects are rendered through the light map and are excluded from v1.");
                    continue;
                }
                if (!IsVisible(item, options)) continue;
                if (item.View?.Animator == null)
                {
                    AddUnsupported(manifest, sourcePath, "missing-resource", "The visible Obj resource has no loaded animator.");
                    continue;
                }
                if (!(item.View.Animator is FrameAnimator) && !(item.View.Animator is ISpineAnimator))
                {
                    AddUnsupported(manifest, sourcePath, "custom-shader", "Custom sprite/shader Obj resources are not exported in v1.");
                    continue;
                }

                var layer = new MapCompositionLayer
                {
                    Id = "obj-" + sourceLayer.ToString("D2", CultureInfo.InvariantCulture) + "-" + item.Index.ToString("D4", CultureInfo.InvariantCulture),
                    Name = string.IsNullOrWhiteSpace(item.Name) ? sourcePath : item.Name,
                    SemanticLayer = "Obj",
                    StartFrame = 0,
                    SourcePath = sourcePath,
                    SourceLayer = sourceLayer,
                    SourceZ = item.Z,
                    SourceIndex = item.Index,
                    SourceTimeOffsetMs = GetLayerSourceTimeOffset(item.View.Animator),
                    FlipX = item.View.Flip
                };
                descriptors.Add(new LayerDescriptor(layer, item.View.Animator, containerOrder, item.Index, item));
                if (item.Events.Count > 0)
                {
                    AddUnsupported(manifest, sourcePath + "/event", "input-event", "Input/collision driven object events are frozen at export time.");
                }
            }
        }

        private void AddTileContainer(
            ContainerNode container,
            int sourceLayer,
            int containerOrder,
            MapCompositionExportOptions options,
            MapCompositionManifest manifest,
            List<LayerDescriptor> descriptors)
        {
            foreach (TileItem item in container.Slots.OfType<TileItem>())
            {
                string sourcePath = GetTileSourcePath(item);
                if (!IsVisible(item, options)) continue;
                if (!(item.View?.Animator is FrameAnimator))
                {
                    AddUnsupported(manifest, sourcePath, "missing-resource", "The visible Tile resource has no PNG frame animator.");
                    continue;
                }

                var layer = new MapCompositionLayer
                {
                    Id = "tile-" + sourceLayer.ToString("D2", CultureInfo.InvariantCulture) + "-" + item.Index.ToString("D4", CultureInfo.InvariantCulture),
                    Name = string.IsNullOrWhiteSpace(item.Name) ? sourcePath : item.Name,
                    SemanticLayer = "Tile",
                    StartFrame = 0,
                    SourcePath = sourcePath,
                    SourceLayer = sourceLayer,
                    SourceZ = 0,
                    SourceIndex = item.Index,
                    SourceTimeOffsetMs = GetLayerSourceTimeOffset(item.View.Animator)
                };
                descriptors.Add(new LayerDescriptor(layer, item.View.Animator, containerOrder, item.Index, item));
            }
        }

        private MapCompositionAsset GetOrCreateAsset(
            LayerDescriptor descriptor,
            string stageDirectory,
            MapCompositionExportOptions options,
            int durationFrames,
            MapCompositionManifest manifest,
            CancellationToken cancellationToken)
        {
            if (descriptor.Animator is FrameAnimator frameAnimator)
            {
                return GetOrCreateFrameAsset(frameAnimator, stageDirectory, options.FrameRate, manifest, cancellationToken);
            }
            if (descriptor.Animator is ISpineAnimator spineAnimator)
            {
                return GetOrCreateSpineAsset(spineAnimator, descriptor.Layer.SourcePath, stageDirectory, options,
                    durationFrames, manifest, cancellationToken);
            }
            return null;
        }

        private MapCompositionAsset GetOrCreateFrameAsset(
            FrameAnimator animator,
            string stageDirectory,
            int frameRate,
            MapCompositionManifest manifest,
            CancellationToken cancellationToken)
        {
            bool loop = !(animator is RepeatableFrameAnimator repeatable) || repeatable.IsLoop;
            FrameAssetCacheEntry cached = frameAssetCache.FirstOrDefault(entry => ReferenceEquals(entry.Data, animator.Data) && entry.Loop == loop);
            if (cached != null) return cached.Asset;

            var asset = new MapCompositionAsset
            {
                Id = NextAssetId(),
                Kind = "png-animation",
                Loop = loop,
                AlphaMode = "straight"
            };
            int cumulativeMs = 0;
            int cumulativeFrames = 0;
            var blendModes = new HashSet<string>(StringComparer.Ordinal);
            int frameIndex = 0;
            foreach (Frame frame in animator.Data.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int delayMs = Math.Max(1, frame?.Delay ?? 1);
                cumulativeMs = checked(cumulativeMs + delayMs);
                int nextFrame = Math.Max(cumulativeFrames + 1,
                    (int)Math.Round(cumulativeMs * frameRate / 1000d, MidpointRounding.AwayFromZero));
                int duration = nextFrame - cumulativeFrames;
                cumulativeFrames = nextFrame;

                MapTexturePngEncoder.EncodedPng png = MapTexturePngEncoder.Encode(frame);
                string hash = ComputeSha256(png.Bytes);
                // The downstream contract deliberately requires one package path per
                // logical frame. The animation-data cache still ensures a shared WZ
                // animation is emitted only once, while repeated pixels in distinct
                // logical frames retain their own timing/addressable frame path.
                string relativePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "assets/png/{0}/frame_{1:D6}.png",
                    asset.Id,
                    frameIndex++);
                string fullPath = MapCompositionManifestValidator.ResolvePath(
                    NormalizeRoot(stageDirectory), relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllBytes(fullPath, png.Bytes);
                managedFiles.Add(relativePath);
                string blendMode = frame?.Blend == true ? "additive" : "normal";
                blendModes.Add(blendMode);
                asset.Frames.Add(new MapCompositionAssetFrame
                {
                    Path = relativePath,
                    Sha256 = hash,
                    Width = png.Width,
                    Height = png.Height,
                    DelayMs = delayMs,
                    DurationFrames = duration,
                    OriginX = frame?.Origin.X ?? 0,
                    OriginY = frame?.Origin.Y ?? 0,
                    A0 = ClampAlpha(frame?.A0 ?? 255),
                    A1 = ClampAlpha(frame?.A1 ?? 255),
                    BlendMode = blendMode
                });
            }
            if (asset.Frames.Count == 0)
            {
                throw new InvalidDataException("PNG animation has no frames: " + asset.Id);
            }
            asset.BlendMode = blendModes.Count == 1 ? blendModes.First() : "normal";
            if (blendModes.Count > 1)
            {
                AddUnsupported(manifest, asset.Id, "mixed-frame-blend",
                    "Per-frame normal/additive blend changes are recorded on frames but may require interval splitting downstream.");
            }

            manifest.Assets.Add(asset);
            frameAssetCache.Add(new FrameAssetCacheEntry(animator.Data, loop, asset));
            return asset;
        }

        private MapCompositionAsset GetOrCreateSpineAsset(
            ISpineAnimator animator,
            string sourcePath,
            string stageDirectory,
            MapCompositionExportOptions options,
            int durationFrames,
            MapCompositionManifest manifest,
            CancellationToken cancellationToken)
        {
            string animationName = animator.SelectedAnimationName;
            string skinName = animator.SelectedSkin;
            int phaseOffset = Math.Max(0, animator.CurrentTime);
            SpineAssetCacheEntry cached = spineAssetCache.FirstOrDefault(entry => ReferenceEquals(entry.Data, animator.Data)
                && string.Equals(entry.AnimationName, animationName, StringComparison.Ordinal)
                && string.Equals(entry.SkinName, skinName, StringComparison.Ordinal)
                && entry.PhaseOffset == phaseOffset);
            if (cached != null) return cached.Asset;

            IMapSpineSequenceBaker baker = options.SpineBaker;
            MapRenderSpineSequenceBaker defaultBaker = null;
            if (baker == null && !MapRenderSpineSequenceBaker.TryCreate(animator, out defaultBaker))
            {
                AddUnsupported(manifest, sourcePath, "spine",
                    "No active GraphicsDevice could be resolved for the Spine v2/v4 sequence baker.");
                return null;
            }
            if (baker == null) baker = defaultBaker;

            string assetId = NextAssetId();
            string relativeDirectory = "assets/spine/" + assetId;
            MapSpineBakeResult bakeResult;
            try
            {
                bakeResult = baker.Bake(new MapSpineBakeRequest
                {
                    PackageRootDirectory = stageDirectory,
                    RelativeOutputDirectory = relativeDirectory,
                    AssetId = assetId,
                    FrameRate = options.FrameRate,
                    DurationFrames = durationFrames,
                    Animator = animator,
                    AnimationName = animationName,
                    SkinName = skinName
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to bake Spine sequence: " + sourcePath, ex);
            }
            if (bakeResult?.Frames == null || bakeResult.Frames.Count == 0)
            {
                throw new InvalidDataException("The Spine baker returned no frames: " + sourcePath);
            }

            var asset = new MapCompositionAsset
            {
                Id = assetId,
                Kind = "spine-sequence",
                Loop = bakeResult.Loop,
                AlphaMode = bakeResult.AlphaMode ?? "straight",
                BlendMode = NormalizeBlendMode(bakeResult.BlendMode),
                AnimationName = animationName ?? string.Empty,
                SkinName = skinName ?? string.Empty,
                SpineVersion = animator.Data.SpineVersion.ToString().ToLowerInvariant(),
                SourcePremultipliedAlpha = animator.Data.PremultipliedAlpha
            };
            foreach (MapSpineBakeFrame frame in bakeResult.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = MapCompositionManifestValidator.NormalizeRelativePath(frame.RelativePath);
                string fullPath = MapCompositionManifestValidator.ResolvePath(NormalizeRoot(stageDirectory), relativePath);
                if (!File.Exists(fullPath)) throw new FileNotFoundException("The Spine baker did not create a declared frame.", fullPath);
                string hash = MapCompositionManifestValidator.ComputeSha256(fullPath);
                managedFiles.Add(relativePath);
                asset.Frames.Add(new MapCompositionAssetFrame
                {
                    Path = relativePath,
                    Sha256 = hash,
                    Width = frame.Width,
                    Height = frame.Height,
                    DelayMs = Math.Max(1, frame.DelayMs),
                    DurationFrames = Math.Max(1, frame.DurationFrames),
                    OriginX = frame.OriginX,
                    OriginY = frame.OriginY,
                    A0 = 255,
                    A1 = 255,
                    BlendMode = asset.BlendMode
                });
            }
            foreach (string warning in bakeResult.Warnings ?? Enumerable.Empty<string>())
            {
                AddUnsupported(manifest, sourcePath, "spine-bake-warning", warning);
            }
            manifest.Assets.Add(asset);
            spineAssetCache.Add(new SpineAssetCacheEntry(animator.Data, animationName, skinName, phaseOffset, asset));
            return asset;
        }

        private static void PopulateLayerTimeline(
            LayerDescriptor descriptor,
            MapCompositionExportOptions options,
            Rectangle worldRect,
            int durationFrames,
            CancellationToken cancellationToken)
        {
            descriptor.Layer.EndFrame = durationFrames;
            ConfigureBackRepeat(descriptor, options, worldRect);
            bool dynamic = IsDynamic(descriptor);
            int sampleCount = dynamic ? durationFrames : 1;
            for (int frameIndex = 0; frameIndex < sampleCount; frameIndex++)
            {
                if ((frameIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                TransformState state = EvaluateTransform(descriptor, options, frameIndex);
                descriptor.Layer.TransformKeys.Add(new MapCompositionTransformKey
                {
                    Frame = frameIndex,
                    X = state.X,
                    Y = state.Y,
                    Opacity = state.Opacity * 100f / 255f,
                    ScaleX = state.FlipX ? -100f : 100f,
                    ScaleY = 100f,
                    Rotation = 0f
                });
            }
            MapCompositionTransformKey first = descriptor.Layer.TransformKeys[0];
            descriptor.Layer.PositionX = first.X;
            descriptor.Layer.PositionY = first.Y;
            descriptor.Layer.FlipX = first.ScaleX < 0f;
            descriptor.Layer.BlendMode = "normal";
        }

        private static void ConfigureBackRepeat(LayerDescriptor descriptor, MapCompositionExportOptions options, Rectangle worldRect)
        {
            if (!(descriptor.Item is BackItem back) || back.TileMode == TileMode.None) return;
            GetBackTileSize(back, out int tileWidth, out int tileHeight);
            descriptor.Layer.TileWidth = tileWidth;
            descriptor.Layer.TileHeight = tileHeight;

            TransformState baseState = EvaluateTransform(descriptor, options, 0, false);
            int minX = 0;
            int maxX = 0;
            int minY = 0;
            int maxY = 0;
            if ((back.TileMode & TileMode.Horizontal) != 0 && tileWidth > 0)
            {
                minX = (int)Math.Floor((worldRect.Left - baseState.X) / tileWidth) - 2;
                maxX = (int)Math.Ceiling((worldRect.Right - baseState.X) / tileWidth) + 2;
            }
            if ((back.TileMode & TileMode.Vertical) != 0 && tileHeight > 0)
            {
                minY = (int)Math.Floor((worldRect.Top - baseState.Y) / tileHeight) - 2;
                maxY = (int)Math.Ceiling((worldRect.Bottom - baseState.Y) / tileHeight) + 2;
            }
            descriptor.RepeatOffsetX = minX * tileWidth;
            descriptor.RepeatOffsetY = minY * tileHeight;
            long repeatedInstances = (long)Math.Max(1, maxX - minX + 1) * Math.Max(1, maxY - minY + 1);
            if (repeatedInstances > MapCompositionManifestValidator.MaxRepeatedInstances)
            {
                throw new InvalidDataException("Tiled Back layer '" + descriptor.Layer.Name
                    + "' requires more than 10000 AE instances. Reduce the CaptureRect/world bounds.");
            }
            descriptor.Layer.Repeat = new MapCompositionRepeat
            {
                CountX = Math.Max(1, maxX - minX + 1),
                CountY = Math.Max(1, maxY - minY + 1),
                StepX = tileWidth,
                StepY = tileHeight
            };
        }

        private static void BuildOrderIntervals(
            List<LayerDescriptor> descriptors,
            MapCompositionExportOptions options,
            int durationFrames,
            MapCompositionManifest manifest,
            CancellationToken cancellationToken)
        {
            MapCompositionOrderInterval current = null;
            string currentKey = null;
            for (int frameIndex = 0; frameIndex < durationFrames; frameIndex++)
            {
                if ((frameIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                List<LayerDescriptor> ordered = descriptors
                    .OrderBy(descriptor => descriptor.ContainerOrder)
                    .ThenBy(descriptor => GetZ0(descriptor, options, frameIndex))
                    .ThenBy(descriptor => descriptor.Z1)
                    .ThenBy(descriptor => descriptor.Layer.Id, StringComparer.Ordinal)
                    .ToList();
                string key = string.Join("\n", ordered.Select(descriptor => descriptor.Layer.Id));
                if (!string.Equals(key, currentKey, StringComparison.Ordinal))
                {
                    if (current != null) current.EndFrame = frameIndex;
                    current = new MapCompositionOrderInterval
                    {
                        StartFrame = frameIndex,
                        EndFrame = durationFrames,
                        LayerIds = ordered.Select(descriptor => descriptor.Layer.Id).ToList()
                    };
                    manifest.OrderIntervals.Add(current);
                    currentKey = key;
                }
            }

            MapCompositionOrderInterval first = manifest.OrderIntervals.First();
            for (int i = 0; i < first.LayerIds.Count; i++)
            {
                MapCompositionLayer layer = manifest.Layers.First(item => item.Id == first.LayerIds[i]);
                layer.DrawOrder = i;
                MapCompositionAsset asset = manifest.Assets.First(item => item.Id == layer.AssetId);
                layer.BlendMode = asset.BlendMode;
            }
        }

        private static int GetZ0(LayerDescriptor descriptor, MapCompositionExportOptions options, int frameIndex)
        {
            if (descriptor.Item is ObjItem obj) return obj.Z;
            if (descriptor.Item is TileItem && descriptor.Animator is FrameAnimator frameAnimator)
            {
                int timeMs = GetAnimatorTime(frameAnimator) + FrameToMilliseconds(frameIndex, options.FrameRate);
                Frame frame = GetFrameAt(frameAnimator, timeMs);
                return frame?.Z ?? 0;
            }
            return 0;
        }

        private static TransformState EvaluateTransform(
            LayerDescriptor descriptor,
            MapCompositionExportOptions options,
            int frameIndex,
            bool applyRepeatOffset = true)
        {
            int elapsedMs = FrameToMilliseconds(frameIndex, options.FrameRate);
            TransformState state;
            if (descriptor.Item is BackItem back)
            {
                int timeMs = (back.View?.Time ?? 0) + elapsedMs;
                GetBackTileSize(back, out int tileWidth, out int tileHeight);
                float x = back.X;
                float y = back.Y;
                if ((back.TileMode & TileMode.ScrollHorizontal) != 0 && tileWidth != 0)
                {
                    x += (float)(((double)back.Rx * 5d * timeMs / 1000d) % tileWidth);
                }
                else
                {
                    x += (options.RenderCameraCenterX ?? options.CameraCenterX ?? 0f) * (100 + back.Rx) / 100f;
                }
                if ((back.TileMode & TileMode.ScrollVertical) != 0 && tileHeight != 0)
                {
                    y += (float)(((double)back.Ry * 5d * timeMs / 1000d) % tileHeight);
                }
                else
                {
                    y += (options.RenderCameraCenterY ?? options.CameraCenterY ?? 0f) * (100 + back.Ry) / 100f;
                }
                state = new TransformState((float)Math.Floor(x), (float)Math.Floor(y), back.Alpha, back.Flip);
            }
            else if (descriptor.Item is ObjItem obj)
            {
                state = EvaluateMovingObject(obj, (obj.View?.Time ?? 0) + elapsedMs);
                state.X += obj.X;
                state.Y += obj.Y;
            }
            else if (descriptor.Item is TileItem tile)
            {
                state = new TransformState(tile.X, tile.Y, 255, false);
            }
            else
            {
                state = new TransformState(0f, 0f, 255, false);
            }

            if (applyRepeatOffset)
            {
                state.X += descriptor.RepeatOffsetX;
                state.Y += descriptor.RepeatOffsetY;
            }
            return state;
        }

        private static TransformState EvaluateMovingObject(ObjItem obj, int timeMs)
        {
            bool flip = obj.View?.Flip ?? obj.Flip;
            int alpha = obj.View?.Alpha ?? 255;
            if (obj.MoveNodes.Count == 0 || obj.TotalMovePeriod <= 0)
            {
                return new TransformState(0f, 0f, alpha, flip);
            }

            int cycleTime = PositiveModulo(timeMs, obj.TotalMovePeriod);
            MoveNode moveNode = obj.MoveNodes.FirstOrDefault(node => cycleTime < node.StartTime + node.TotalPeriod);
            if (moveNode == null || moveNode.MoveP <= 0)
            {
                return new TransformState(0f, 0f, alpha, flip);
            }

            double time = timeMs;
            float x = 0f;
            float y = 0f;
            switch (obj.MoveType)
            {
                case 1:
                case 2:
                    time *= Math.PI * 2d / moveNode.MoveP;
                    x = (float)(moveNode.MoveW * Math.Cos(time));
                    y = (float)(moveNode.MoveH * Math.Cos(time));
                    break;
                case 3:
                    time *= Math.PI * 2d / moveNode.MoveP;
                    x = (float)(moveNode.MoveW * Math.Cos(time));
                    y = (float)(moveNode.MoveH * Math.Sin(time));
                    break;
                case 6:
                    time = PositiveModulo(timeMs, obj.TotalMovePeriod) - moveNode.StartTime;
                    float oneWayProgress = Clamp01((float)((time - moveNode.MoveDelay) / moveNode.MoveP));
                    x = MathHelper.Lerp(0f, moveNode.MoveW, oneWayProgress) + moveNode.StartPos.X;
                    y = MathHelper.Lerp(0f, moveNode.MoveH, oneWayProgress) + moveNode.StartPos.Y;
                    float fadeTime = Math.Min(moveNode.MoveP * 0.1f, 300f);
                    float alphaProgress = 1f;
                    if (fadeTime > 0f && moveNode.IsFirstNode && time <= moveNode.MoveDelay + fadeTime)
                    {
                        alphaProgress = Clamp01((float)(time - moveNode.MoveDelay) / fadeTime);
                    }
                    else if (fadeTime > 0f && moveNode.IsLastNode && time >= moveNode.MoveDelay + moveNode.MoveP - fadeTime)
                    {
                        alphaProgress = Clamp01((float)(moveNode.MoveDelay + moveNode.MoveP - time) / fadeTime);
                    }
                    alpha = (int)MathHelper.Lerp(0f, 255f, alphaProgress);
                    break;
                case 7:
                case 8:
                    double period = (moveNode.MoveDelay + moveNode.MoveP) * 2d;
                    time = period <= 0d ? 0d : PositiveModulo(timeMs, (int)period);
                    bool backwards = time >= period / 2d;
                    if (backwards) time -= period / 2d;
                    if (obj.MoveType == 8) flip = backwards ? !obj.Flip : obj.Flip;
                    float progress = Clamp01((float)((time - moveNode.MoveDelay) / moveNode.MoveP));
                    if (backwards) progress = 1f - progress;
                    x = MathHelper.Lerp(0f, moveNode.MoveW, progress);
                    y = MathHelper.Lerp(0f, moveNode.MoveH, progress);
                    break;
            }
            return new TransformState(x, y, alpha, flip);
        }

        private static Frame GetFrameAt(FrameAnimator animator, int timeMs)
        {
            IList<Frame> frames = animator.Data.Frames;
            if (frames.Count == 0) return null;
            int length = frames.Sum(frame => Math.Max(1, frame.Delay));
            if (length <= 0) return frames[0];
            bool loop = !(animator is RepeatableFrameAnimator repeatable) || repeatable.IsLoop;
            int time = loop ? PositiveModulo(timeMs, length) : Math.Min(Math.Max(0, timeMs), length - 1);
            int end = 0;
            foreach (Frame frame in frames)
            {
                end += Math.Max(1, frame.Delay);
                if (time < end) return frame;
            }
            return frames[frames.Count - 1];
        }

        private static bool IsDynamic(LayerDescriptor descriptor)
        {
            if (descriptor.Item is ObjItem obj) return obj.MoveNodes.Count > 0;
            if (descriptor.Item is BackItem back)
            {
                return (back.TileMode & (TileMode.ScrollHorizontal | TileMode.ScrollVertical)) != 0;
            }
            return false;
        }

        private static void GetBackTileSize(BackItem back, out int width, out int height)
        {
            width = back.Cx;
            height = back.Cy;
            if ((back.TileMode & TileMode.BothTile) != 0 && (width == 0 || height == 0))
            {
                Rectangle bound = Rectangle.Empty;
                if (back.View?.Animator is FrameAnimator frameAnimator) bound = frameAnimator.Data.GetBound();
                else if (back.View?.Animator is AnimationItem animation) bound = animation.Measure();
                if (width == 0) width = bound.Width;
                if (height == 0) height = bound.Height;
            }
        }

        private static bool IsVisible(BackItem item, MapCompositionExportOptions options, bool front)
        {
            PatchVisibility visibility = options.Visibility;
            if (item.ScreenMode != 0 && item.ScreenMode != options.DisplayMode + 1) return false;
            if (visibility == null) return true;
            if (front ? !visibility.FrontVisible : !visibility.BackVisible) return false;
            if (HasHiddenTag(item.Tags, visibility)) return false;
            return !item.Quest.Any(quest => !visibility.IsQuestVisible(quest.ID, quest.State));
        }

        private static bool IsVisible(ObjItem item, MapCompositionExportOptions options)
        {
            PatchVisibility visibility = options.Visibility;
            if (visibility == null) return true;
            if (!visibility.ObjVisible || HasHiddenTag(item.Tags, visibility)) return false;
            if (item.Quest.Any(quest => !visibility.IsQuestVisible(quest.ID, quest.State))) return false;
            return !item.Questex.Any(quest => !visibility.IsQuestVisible(quest.ID, quest.Key, quest.State));
        }

        private static bool IsVisible(TileItem item, MapCompositionExportOptions options)
        {
            return options.Visibility == null || options.Visibility.TileVisible;
        }

        private static bool HasHiddenTag(string[] tags, PatchVisibility visibility)
        {
            return tags != null && tags.Any(tag => !visibility.IsTagVisible(tag));
        }

        private static MapCompositionVisibility CreateVisibilitySnapshot(MapCompositionExportOptions options)
        {
            PatchVisibility visibility = options.Visibility;
            var snapshot = new MapCompositionVisibility
            {
                DisplayMode = options.DisplayMode,
                DefaultTagVisible = visibility?.DefaultTagVisible ?? true
            };
            snapshot.Categories["back"] = visibility?.BackVisible ?? true;
            snapshot.Categories["obj"] = visibility?.ObjVisible ?? true;
            snapshot.Categories["tile"] = visibility?.TileVisible ?? true;
            snapshot.Categories["front"] = visibility?.FrontVisible ?? true;
            if (visibility != null)
            {
                foreach (KeyValuePair<string, bool> pair in visibility.TagsVisible)
                {
                    snapshot.Tags[pair.Key] = pair.Value;
                }
                foreach (KeyValuePair<int, int> pair in visibility.QuestVisible.OrderBy(pair => pair.Key))
                {
                    snapshot.Quests[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value;
                }
                foreach (KeyValuePair<Tuple<int, string>, int> pair in visibility.QuestExVisible
                    .OrderBy(pair => pair.Key.Item1)
                    .ThenBy(pair => pair.Key.Item2, StringComparer.Ordinal))
                {
                    string key = pair.Key.Item1.ToString(CultureInfo.InvariantCulture) + ":" + (pair.Key.Item2 ?? string.Empty);
                    snapshot.QuestEx[key] = pair.Value;
                }
            }
            return snapshot;
        }

        private static void AddUnsupportedSceneItems(MapData mapData, MapCompositionManifest manifest)
        {
            var seen = new HashSet<SceneItem>(ReferenceEqualityComparer<SceneItem>.Instance);
            foreach (SceneNode node in new[] { mapData.Scene }.Concat(mapData.Scene.Descendants()))
            {
                if (!(node is ContainerNode container)) continue;
                foreach (SceneItem item in container.Slots)
                {
                    if (!seen.Add(item) || item is BackItem || item is ObjItem || item is TileItem) continue;
                    AddUnsupported(manifest, item.Name ?? item.GetType().Name, GetUnsupportedKind(item),
                        "This scene item type is outside the Back/Tile/Obj/Front v1 export scope.");
                }
            }
            if (mapData.Light != null) AddUnsupported(manifest, "light", "light", "Map light/shader output is excluded from v1.");
            if (!string.IsNullOrEmpty(mapData.Bgm)) AddUnsupported(manifest, mapData.Bgm, "bgm", "BGM is excluded from v1.");
            for (int i = 0; i < mapData.MapEvents.Count; i++)
            {
                AddUnsupported(manifest, "effect/" + i.ToString(CultureInfo.InvariantCulture), "map-event",
                    "Input-driven map events are frozen and excluded from v1.");
            }
        }

        private static string GetUnsupportedKind(SceneItem item)
        {
            string name = item.GetType().Name;
            if (name.EndsWith("Item", StringComparison.Ordinal)) name = name.Substring(0, name.Length - 4);
            return name.ToLowerInvariant();
        }

        private static int ValidateOptions(MapCompositionExportOptions options, Rectangle worldRect)
        {
            if (options.FrameRate <= 0 || options.FrameRate > 120) throw new ArgumentOutOfRangeException(nameof(options.FrameRate));
            if (double.IsNaN(options.DurationSeconds) || double.IsInfinity(options.DurationSeconds) || options.DurationSeconds <= 0d)
                throw new ArgumentOutOfRangeException(nameof(options.DurationSeconds));
            int durationFrames = checked((int)Math.Round(options.DurationSeconds * options.FrameRate, MidpointRounding.AwayFromZero));
            if (durationFrames <= 0 || durationFrames > options.FrameRate * MapCompositionManifestValidator.MaxDurationSeconds)
                throw new ArgumentOutOfRangeException(nameof(options.DurationSeconds), "Duration must not exceed three hours.");
            if (worldRect.Width <= 0 || worldRect.Height <= 0
                || worldRect.Width > MapCompositionManifestValidator.MaxCompositionDimension
                || worldRect.Height > MapCompositionManifestValidator.MaxCompositionDimension)
                throw new ArgumentOutOfRangeException(nameof(options.WorldRect), "World bounds must be positive and at most 30000x30000.");
            if (options.ViewportWidth <= 0 || options.ViewportHeight <= 0
                || options.ViewportWidth > MapCompositionManifestValidator.MaxCompositionDimension
                || options.ViewportHeight > MapCompositionManifestValidator.MaxCompositionDimension)
                throw new ArgumentOutOfRangeException(nameof(options.ViewportWidth), "Viewport must be positive and at most 30000x30000.");
            if (float.IsNaN(options.CameraScale) || float.IsInfinity(options.CameraScale) || options.CameraScale <= 0f)
                throw new ArgumentOutOfRangeException(nameof(options.CameraScale), "Camera scale must be finite and positive.");
            return durationFrames;
        }

        private void ResetState()
        {
            frameAssetCache.Clear();
            spineAssetCache.Clear();
            managedFiles.Clear();
            nextAssetId = 1;
        }

        private string NextAssetId()
        {
            return "asset-" + nextAssetId++.ToString("D5", CultureInfo.InvariantCulture);
        }

        private static int GetAnimatorTime(object animator)
        {
            if (animator is ISpineAnimator spine) return Math.Max(0, spine.CurrentTime);
            if (animator is FrameAnimator frame) return Math.Max(0, frame.CurrentTime);
            return 0;
        }

        private static int GetLayerSourceTimeOffset(object animator)
        {
            // Spine sequences are baked beginning at the live phase. PNG assets remain
            // source-frame animations, so the downstream scheduler applies this offset.
            return animator is ISpineAnimator ? 0 : GetAnimatorTime(animator);
        }

        private static int FrameToMilliseconds(int frameIndex, int frameRate)
        {
            return (int)Math.Round(frameIndex * 1000d / frameRate, MidpointRounding.AwayFromZero);
        }

        private static int PositiveModulo(int value, int divisor)
        {
            if (divisor <= 0) return 0;
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private static float Clamp01(float value)
        {
            return Math.Max(0f, Math.Min(1f, value));
        }

        private static int ClampAlpha(int value)
        {
            return Math.Max(0, Math.Min(255, value));
        }

        private static string NormalizeBlendMode(string blendMode)
        {
            switch ((blendMode ?? string.Empty).ToLowerInvariant())
            {
                case "add":
                case "additive": return "additive";
                case "screen": return "screen";
                case "multiply": return "multiply";
                default: return "normal";
            }
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }

        private static MapCompositionRect ToManifestRect(Rectangle rect)
        {
            return new MapCompositionRect { X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };
        }

        private static string NormalizeRoot(string root)
        {
            return Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }

        private static string GetBackSourcePath(BackItem item)
        {
            string directory = item.Ani == 0 ? "back" : item.Ani == 1 ? "ani" : "spine" + item.SpineNo;
            return "Map/Back/" + item.BS + ".img/" + directory + "/" + item.No;
        }

        private static string GetObjSourcePath(ObjItem item)
        {
            return "Map/Obj/" + item.OS + ".img/" + item.L0 + "/" + item.L1 + "/" + item.L2;
        }

        private static string GetTileSourcePath(TileItem item)
        {
            return "Map/Tile/" + item.TS + ".img/" + item.U + "/" + item.No;
        }

        private static void AddUnsupported(MapCompositionManifest manifest, string path, string kind, string reason)
        {
            manifest.Unsupported.Add(new MapCompositionUnsupportedItem { Path = path, Kind = kind, Reason = reason });
        }

        private sealed class LayerDescriptor
        {
            public LayerDescriptor(MapCompositionLayer layer, object animator, int containerOrder, int z1, SceneItem item)
            {
                Layer = layer;
                Animator = animator;
                ContainerOrder = containerOrder;
                Z1 = z1;
                Item = item;
            }

            public MapCompositionLayer Layer { get; }
            public object Animator { get; }
            public int ContainerOrder { get; }
            public int Z1 { get; }
            public SceneItem Item { get; }
            public int RepeatOffsetX { get; set; }
            public int RepeatOffsetY { get; set; }
        }

        private sealed class FrameAssetCacheEntry
        {
            public FrameAssetCacheEntry(FrameAnimationData data, bool loop, MapCompositionAsset asset)
            {
                Data = data;
                Loop = loop;
                Asset = asset;
            }
            public FrameAnimationData Data { get; }
            public bool Loop { get; }
            public MapCompositionAsset Asset { get; }
        }

        private sealed class SpineAssetCacheEntry
        {
            public SpineAssetCacheEntry(ISpineAnimationData data, string animationName, string skinName, int phaseOffset, MapCompositionAsset asset)
            {
                Data = data;
                AnimationName = animationName;
                SkinName = skinName;
                PhaseOffset = phaseOffset;
                Asset = asset;
            }
            public ISpineAnimationData Data { get; }
            public string AnimationName { get; }
            public string SkinName { get; }
            public int PhaseOffset { get; }
            public MapCompositionAsset Asset { get; }
        }

        private struct TransformState
        {
            public TransformState(float x, float y, int opacity, bool flipX)
            {
                X = x;
                Y = y;
                Opacity = ClampAlpha(opacity);
                FlipX = flipX;
            }
            public float X;
            public float Y;
            public int Opacity;
            public bool FlipX;
        }

        private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();
            public bool Equals(T x, T y) => ReferenceEquals(x, y);
            public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }

    internal static class MapCompositionPackageCommitter
    {
        public static void Commit(string stageDirectory, string outputDirectory)
        {
            string parent = Directory.GetParent(outputDirectory)?.FullName;
            if (parent == null) throw new InvalidOperationException("The output directory has no parent.");
            string backup = Path.Combine(parent, "." + new DirectoryInfo(outputDirectory).Name + ".map-backup-" + Guid.NewGuid().ToString("N"));
            bool movedOld = false;
            try
            {
                if (Directory.Exists(outputDirectory))
                {
                    Directory.Move(outputDirectory, backup);
                    movedOld = true;
                }
                Directory.Move(stageDirectory, outputDirectory);
            }
            catch
            {
                if (!Directory.Exists(outputDirectory) && movedOld && Directory.Exists(backup))
                {
                    Directory.Move(backup, outputDirectory);
                }
                throw;
            }

            if (Directory.Exists(backup))
            {
                try { Directory.Delete(backup, true); }
                catch { /* A complete package is committed; leave recoverable backup cleanup to the caller. */ }
            }
        }
    }
}
