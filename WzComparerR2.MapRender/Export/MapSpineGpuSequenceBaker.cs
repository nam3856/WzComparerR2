using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WzComparerR2.Animation;
using WzComparerR2.Controls;
using WzComparerR2.Rendering;

namespace WzComparerR2.MapRender.Export
{
    /// <summary>
    /// Bakes the currently selected Spine v2/v4 animation through the same
    /// SkeletonRenderer and PngEffect path used by the animation preview.
    /// </summary>
    public sealed class MapRenderSpineSequenceBaker : IMapSpineSequenceBaker
    {
        private const int BoundsPadding = 2;
        private readonly GraphicsDevice graphicsDevice;

        public MapRenderSpineSequenceBaker(GraphicsDevice graphicsDevice)
        {
            this.graphicsDevice = graphicsDevice ?? throw new ArgumentNullException(nameof(graphicsDevice));
        }

        public static bool TryCreate(
            GraphicsDevice graphicsDevice,
            IServiceProvider services,
            out MapRenderSpineSequenceBaker baker)
        {
            baker = null;
            GraphicsDevice device = graphicsDevice;
            if ((device == null || device.IsDisposed) && services != null)
            {
                device = (services.GetService(typeof(IGraphicsDeviceService)) as IGraphicsDeviceService)?.GraphicsDevice;
            }
            if (device == null || device.IsDisposed)
            {
                return false;
            }
            baker = new MapRenderSpineSequenceBaker(device);
            return true;
        }

        public static bool TryCreate(ISpineAnimator animator, out MapRenderSpineSequenceBaker baker)
        {
            baker = null;
            GraphicsDevice device = FindGraphicsDevice(animator?.Data?.Atlas);
            if (device == null || device.IsDisposed)
            {
                return false;
            }
            baker = new MapRenderSpineSequenceBaker(device);
            return true;
        }

        public MapSpineBakeResult Bake(MapSpineBakeRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Animator == null) throw new ArgumentException("A Spine animator is required.", nameof(request));
            if (request.FrameRate <= 0) throw new ArgumentOutOfRangeException(nameof(request.FrameRate));
            if (request.DurationFrames <= 0) throw new ArgumentOutOfRangeException(nameof(request.DurationFrames));
            ValidateSelection(request);

            string relativeDirectory = MapCompositionManifestValidator.NormalizeRelativePath(request.RelativeOutputDirectory);
            string outputDirectory = MapCompositionManifestValidator.ResolvePath(
                Path.GetFullPath(request.PackageRootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar,
                relativeDirectory);
            Directory.CreateDirectory(outputDirectory);

            Rectangle union = MeasureUnionBounds(request, cancellationToken, out HashSet<string> blendModes);
            if (union.IsEmpty)
            {
                union = new Rectangle(-BoundsPadding, -BoundsPadding, BoundsPadding * 2 + 1, BoundsPadding * 2 + 1);
            }
            else
            {
                union.Inflate(BoundsPadding, BoundsPadding);
            }
            if (union.Width > MapCompositionManifestValidator.MaxCompositionDimension
                || union.Height > MapCompositionManifestValidator.MaxCompositionDimension)
            {
                throw new InvalidDataException("The Spine union bounds exceed After Effects' 30000 pixel limit.");
            }

            var result = new MapSpineBakeResult
            {
                Loop = true,
                AlphaMode = "straight",
                BlendMode = ResolveLayerBlendMode(blendModes)
            };
            if (blendModes.Count > 1)
            {
                result.Warnings.Add("Spine slot blend modes (" + string.Join(", ", blendModes.OrderBy(value => value, StringComparer.Ordinal))
                    + ") were flattened into one normal straight-alpha sequence.");
                result.BlendMode = "normal";
            }
            else if (blendModes.Any(mode => mode != "normal" && mode != "additive" && mode != "screen" && mode != "multiply"))
            {
                result.Warnings.Add("An unsupported Spine slot blend mode was flattened into a normal straight-alpha sequence.");
                result.BlendMode = "normal";
            }

            BakeFrames(request, union, relativeDirectory, result, cancellationToken);
            return result;
        }

        private Rectangle MeasureUnionBounds(
            MapSpineBakeRequest request,
            CancellationToken cancellationToken,
            out HashSet<string> blendModes)
        {
            blendModes = new HashSet<string>(StringComparer.Ordinal);
            AnimationItem animation = CreateAnimation(request);
            Rectangle union = Rectangle.Empty;
            double previousTime = 0d;
            for (int frameIndex = 0; frameIndex < request.DurationFrames; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sampleTime = frameIndex * 1000d / request.FrameRate;
                if (sampleTime > previousTime)
                {
                    animation.Update(TimeSpan.FromMilliseconds(sampleTime - previousTime));
                }
                previousTime = sampleTime;
                Rectangle bound = animation.Measure();
                if (!bound.IsEmpty)
                {
                    union = union.IsEmpty ? bound : Rectangle.Union(union, bound);
                }
                CollectBlendModes(((ISpineAnimator)animation).Skeleton, blendModes);
            }
            if (blendModes.Count == 0) blendModes.Add("normal");
            return union;
        }

        private void BakeFrames(
            MapSpineBakeRequest request,
            Rectangle union,
            string relativeDirectory,
            MapSpineBakeResult result,
            CancellationToken cancellationToken)
        {
            AnimationItem animation = CreateAnimation(request);
            var spineAnimator = (ISpineAnimator)animation;
            RenderTargetBinding[] previousTargets = graphicsDevice.GetRenderTargets();
            Viewport previousViewport = graphicsDevice.Viewport;
            Rectangle previousScissor = graphicsDevice.ScissorRectangle;
            BlendState previousBlendState = graphicsDevice.BlendState;
            DepthStencilState previousDepthStencilState = graphicsDevice.DepthStencilState;
            RasterizerState previousRasterizerState = graphicsDevice.RasterizerState;
            SamplerState previousSamplerState = graphicsDevice.SamplerStates[0];
            var animationGraphics = new AnimationGraphics(graphicsDevice);
            using (var compositeTarget = new RenderTarget2D(graphicsDevice, union.Width, union.Height, false,
                SurfaceFormat.Bgra32, DepthFormat.None, 0, RenderTargetUsage.PreserveContents))
            using (var straightTarget = new RenderTarget2D(graphicsDevice, union.Width, union.Height, false,
                SurfaceFormat.Bgra32, DepthFormat.None))
            using (var spriteBatch = new SpriteBatch(graphicsDevice))
            using (var pngEffect = new PngEffect(graphicsDevice))
            {
                List<BlendModeOverride> blendModeOverrides = null;
                try
                {
                    if (!string.Equals(result.BlendMode, "normal", StringComparison.Ordinal))
                    {
                        // A sequence with one non-normal slot mode is imported as
                        // one AE layer using that mode. Render its source pixels as
                        // normal here so the blend is applied exactly once by AE.
                        blendModeOverrides = OverrideSlotBlendModesWithNormal(spineAnimator.Skeleton);
                        if (blendModeOverrides.Count == 0)
                        {
                            result.Warnings.Add("The Spine slot blend mode could not be normalized before baking; it was flattened into the PNG sequence.");
                            result.BlendMode = "normal";
                        }
                    }
                    pngEffect.AlphaMixEnabled = false;
                    double previousTime = 0d;
                    bool edgeWarningAdded = false;
                    for (int frameIndex = 0; frameIndex < request.DurationFrames; frameIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double sampleTime = frameIndex * 1000d / request.FrameRate;
                        if (sampleTime > previousTime)
                        {
                            animation.Update(TimeSpan.FromMilliseconds(sampleTime - previousTime));
                        }
                        previousTime = sampleTime;

                        graphicsDevice.SetRenderTarget(compositeTarget);
                        graphicsDevice.Clear(Color.Transparent);
                        Matrix world = Matrix.CreateTranslation(-union.Left, -union.Top, 0f);
                        animationGraphics.Draw(spineAnimator, world);

                        graphicsDevice.SetRenderTarget(straightTarget);
                        graphicsDevice.Clear(Color.Transparent);
                        spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.PointClamp,
                            DepthStencilState.None, RasterizerState.CullNone, pngEffect);
                        spriteBatch.Draw(compositeTarget, Vector2.Zero, Color.White);
                        spriteBatch.End();

                        graphicsDevice.SetRenderTargets(previousTargets);
                        var png = MapTexturePngEncoder.EncodeBgra32(straightTarget);
                        string fileName = "frame_" + frameIndex.ToString("D6", CultureInfo.InvariantCulture) + ".png";
                        string relativePath = relativeDirectory + "/" + fileName;
                        string fullPath = MapCompositionManifestValidator.ResolvePath(
                            Path.GetFullPath(request.PackageRootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar,
                            relativePath);
                        File.WriteAllBytes(fullPath, png.Bytes);

                        if (!edgeWarningAdded && HasVisibleEdge(straightTarget))
                        {
                            result.Warnings.Add("A Spine frame reaches the padded union edge; inspect the sequence for clipping.");
                            edgeWarningAdded = true;
                        }

                        int startMs = (int)Math.Round(frameIndex * 1000d / request.FrameRate, MidpointRounding.AwayFromZero);
                        int endMs = (int)Math.Round((frameIndex + 1) * 1000d / request.FrameRate, MidpointRounding.AwayFromZero);
                        result.Frames.Add(new MapSpineBakeFrame
                        {
                            RelativePath = relativePath,
                            Width = union.Width,
                            Height = union.Height,
                            OriginX = -union.Left,
                            OriginY = -union.Top,
                            DelayMs = Math.Max(1, endMs - startMs),
                            DurationFrames = 1
                        });
                    }
                }
                finally
                {
                    try
                    {
                        RestoreSlotBlendModes(blendModeOverrides);
                    }
                    finally
                    {
                        graphicsDevice.SetRenderTargets(previousTargets);
                        graphicsDevice.Viewport = previousViewport;
                        graphicsDevice.ScissorRectangle = previousScissor;
                        graphicsDevice.BlendState = previousBlendState;
                        graphicsDevice.DepthStencilState = previousDepthStencilState;
                        graphicsDevice.RasterizerState = previousRasterizerState;
                        graphicsDevice.SamplerStates[0] = previousSamplerState;
                        animationGraphics.End(true);
                    }
                }
            }
        }

        private static List<BlendModeOverride> OverrideSlotBlendModesWithNormal(object skeleton)
        {
            var overrides = new List<BlendModeOverride>();
            var visitedData = new List<object>();
            object slots = GetMemberValue(skeleton, "Slots") ?? GetMemberValue(skeleton, "DrawOrder");
            foreach (object slot in EnumerateCollection(slots))
            {
                try
                {
                    object data = GetMemberValue(slot, "Data");
                    if (data == null || visitedData.Any(value => ReferenceEquals(value, data))) continue;
                    visitedData.Add(data);

                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
                    PropertyInfo property = data.GetType().GetProperty("BlendMode", flags)
                        ?? data.GetType().GetProperty("Blend", flags);
                    if (property == null || !property.CanRead || !property.CanWrite) continue;

                    object originalValue = property.GetValue(data, null);
                    Type valueType = property.PropertyType;
                    if (!valueType.IsEnum) continue;
                    object normalValue = Enum.Parse(valueType, "Normal", true);
                    if (Equals(originalValue, normalValue)) continue;

                    property.SetValue(data, normalValue, null);
                    overrides.Add(new BlendModeOverride(data, property, originalValue));
                }
                catch (Exception)
                {
                    continue;
                }
            }
            return overrides;
        }

        private static void RestoreSlotBlendModes(List<BlendModeOverride> overrides)
        {
            if (overrides == null) return;
            Exception firstError = null;
            for (int i = overrides.Count - 1; i >= 0; i--)
            {
                try
                {
                    overrides[i].Restore();
                }
                catch (Exception ex)
                {
                    if (firstError == null) firstError = ex;
                }
            }
            if (firstError != null) throw new InvalidOperationException("Failed to restore Spine slot blend modes after baking.", firstError);
        }

        private static void ValidateSelection(MapSpineBakeRequest request)
        {
            ISpineAnimator animator = request.Animator;
            if (!string.IsNullOrEmpty(request.AnimationName)
                && !animator.Animations.Contains(request.AnimationName))
            {
                throw new InvalidDataException("The selected Spine animation was not found: " + request.AnimationName);
            }
            if (!string.IsNullOrEmpty(request.SkinName)
                && !animator.Skins.Contains(request.SkinName))
            {
                throw new InvalidDataException("The selected Spine skin was not found: " + request.SkinName);
            }
        }

        private static AnimationItem CreateAnimation(MapSpineBakeRequest request)
        {
            var animation = request.Animator.Data.CreateAnimator() as AnimationItem;
            var spine = animation as ISpineAnimator;
            if (animation == null || spine == null)
            {
                throw new InvalidOperationException("The Spine data did not create an AnimationItem.");
            }

            spine.SelectedAnimationName = request.AnimationName;
            if (!string.IsNullOrEmpty(request.SkinName))
            {
                spine.SelectedSkin = request.SkinName;
            }
            animation.Position = Point.Zero;
            int phaseOffset = Math.Max(0, request.Animator.CurrentTime);
            if (phaseOffset > 0)
            {
                animation.Update(TimeSpan.FromMilliseconds(phaseOffset));
            }
            return animation;
        }

        private static bool HasVisibleEdge(Texture2D texture)
        {
            var pixels = new byte[checked(texture.Width * texture.Height * 4)];
            texture.GetData(pixels);
            int width = texture.Width;
            int height = texture.Height;
            for (int x = 0; x < width; x++)
            {
                if (pixels[(x * 4) + 3] != 0 || pixels[(((height - 1) * width + x) * 4) + 3] != 0) return true;
            }
            for (int y = 1; y < height - 1; y++)
            {
                if (pixels[(y * width * 4) + 3] != 0 || pixels[((y * width + width - 1) * 4) + 3] != 0) return true;
            }
            return false;
        }

        private static string ResolveLayerBlendMode(HashSet<string> blendModes)
        {
            return blendModes.Count == 1 ? blendModes.First() : "normal";
        }

        private static void CollectBlendModes(object skeleton, HashSet<string> blendModes)
        {
            object drawOrder = GetMemberValue(skeleton, "DrawOrder");
            if (drawOrder == null) return;

            foreach (object slot in EnumerateCollection(drawOrder))
            {
                // Draw-order slots without a current attachment do not contribute
                // pixels at this sample and must not turn a single-blend sequence
                // into a false mixed-blend warning.
                if (GetMemberValue(slot, "Attachment") == null) continue;
                object data = GetMemberValue(slot, "Data");
                object blend = GetMemberValue(data, "BlendMode") ?? GetMemberValue(data, "Blend");
                if (blend == null) continue;
                string value = blend.ToString().ToLowerInvariant();
                if (value.Contains("add")) value = "additive";
                else if (value.Contains("screen")) value = "screen";
                else if (value.Contains("multiply")) value = "multiply";
                else if (value.Contains("normal")) value = "normal";
                blendModes.Add(value);
            }
        }

        private static IEnumerable<object> EnumerateCollection(object collection)
        {
            if (collection is IEnumerable enumerable)
            {
                foreach (object item in enumerable) yield return item;
                yield break;
            }

            object items = GetMemberValue(collection, "Items");
            object countValue = GetMemberValue(collection, "Count");
            int count = countValue == null ? 0 : Convert.ToInt32(countValue, CultureInfo.InvariantCulture);
            if (items is Array array)
            {
                for (int i = 0; i < count && i < array.Length; i++) yield return array.GetValue(i);
            }
        }

        private static object GetMemberValue(object instance, string name)
        {
            if (instance == null) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0) return property.GetValue(instance, null);
            FieldInfo field = type.GetField(name, flags);
            return field?.GetValue(instance);
        }

        private static GraphicsDevice FindGraphicsDevice(object atlas)
        {
            object pages = GetMemberValue(atlas, "Pages");
            if (pages == null) return null;
            foreach (object page in EnumerateCollection(pages))
            {
                object rendererObject = GetMemberValue(page, "RendererObject") ?? GetMemberValue(page, "rendererObject");
                if (rendererObject is Texture2D texture) return texture.GraphicsDevice;
            }
            return null;
        }

        private sealed class BlendModeOverride
        {
            public BlendModeOverride(object instance, PropertyInfo property, object originalValue)
            {
                this.instance = instance;
                this.property = property;
                this.originalValue = originalValue;
            }

            private readonly object instance;
            private readonly PropertyInfo property;
            private readonly object originalValue;

            public void Restore()
            {
                property.SetValue(instance, originalValue, null);
            }
        }
    }
}
