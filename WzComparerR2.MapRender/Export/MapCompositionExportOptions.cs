using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Xna.Framework;
using WzComparerR2.Animation;

namespace WzComparerR2.MapRender.Export
{
    public sealed class MapCompositionExportOptions
    {
        public int FrameRate { get; set; } = 30;
        public double DurationSeconds { get; set; } = 10d;
        public Rectangle? WorldRect { get; set; }
        public int ViewportWidth { get; set; } = 1920;
        public int ViewportHeight { get; set; } = 1080;
        public float? CameraCenterX { get; set; }
        public float? CameraCenterY { get; set; }
        public float CameraScale { get; set; } = 1f;
        /// <summary>
        /// Raw, zoom-scaled Camera.Center used by MapRender's Back parallax formula.
        /// When omitted, the world-space camera center is used.
        /// </summary>
        public float? RenderCameraCenterX { get; set; }
        public float? RenderCameraCenterY { get; set; }
        public int DisplayMode { get; set; }
        public string DisplayName { get; set; }
        public PatchVisibility Visibility { get; set; }
        public IMapSpineSequenceBaker SpineBaker { get; set; }
    }

    public sealed class MapCompositionExportProgress
    {
        public MapCompositionExportProgress(string phase, int completed, int total)
        {
            Phase = phase;
            Completed = completed;
            Total = total;
        }

        public string Phase { get; }
        public int Completed { get; }
        public int Total { get; }
    }

    public sealed class MapCompositionExportResult
    {
        public string OutputDirectory { get; internal set; }
        public string ManifestPath { get; internal set; }
        public MapCompositionManifest Manifest { get; internal set; }
        public int AssetCount => Manifest?.Assets?.Count ?? 0;
        public int LayerCount => Manifest?.Layers?.Count ?? 0;
        public int WarningCount => Manifest?.Unsupported?.Count ?? 0;
    }

    /// <summary>
    /// GPU-specific Spine rendering boundary. The caller supplies an implementation
    /// backed by the active MapRender GraphicsDevice. Implementations must render the
    /// requested animation with one union bound/origin and write straight-alpha PNGs
    /// below <see cref="MapSpineBakeRequest.PackageRootDirectory"/>.
    /// </summary>
    public interface IMapSpineSequenceBaker
    {
        MapSpineBakeResult Bake(MapSpineBakeRequest request, CancellationToken cancellationToken);
    }

    public sealed class MapSpineBakeRequest
    {
        public string PackageRootDirectory { get; internal set; }
        public string RelativeOutputDirectory { get; internal set; }
        public string AssetId { get; internal set; }
        public int FrameRate { get; internal set; }
        public int DurationFrames { get; internal set; }
        public ISpineAnimator Animator { get; internal set; }
        public string AnimationName { get; internal set; }
        public string SkinName { get; internal set; }
    }

    public sealed class MapSpineBakeResult
    {
        public bool Loop { get; set; } = true;
        public string AlphaMode { get; set; } = "straight";
        public string BlendMode { get; set; } = "normal";
        public List<MapSpineBakeFrame> Frames { get; set; } = new List<MapSpineBakeFrame>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class MapSpineBakeFrame
    {
        public string RelativePath { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        public int DelayMs { get; set; }
        public int DurationFrames { get; set; }
    }
}
