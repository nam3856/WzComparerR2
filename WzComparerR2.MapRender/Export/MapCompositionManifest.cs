using System.Collections.Generic;

namespace WzComparerR2.MapRender.Export
{
    /// <summary>
    /// Versioned, self-contained description of a MapRender scene for downstream
    /// composition builders. All paths in this document are package-relative and
    /// use forward slashes.
    /// </summary>
    public sealed class MapCompositionManifest
    {
        public const string SchemaName = "wzcomparer-map-composition";
        public const int CurrentVersion = 1;

        public string Schema { get; set; } = SchemaName;
        public int Version { get; set; } = CurrentVersion;
        public MapCompositionMapInfo Map { get; set; } = new MapCompositionMapInfo();
        public MapCompositionTimeline Timeline { get; set; } = new MapCompositionTimeline();
        public MapCompositionRect WorldRect { get; set; } = new MapCompositionRect();
        public MapCompositionViewport Viewport { get; set; } = new MapCompositionViewport();
        public MapCompositionVisibility Visibility { get; set; } = new MapCompositionVisibility();
        public List<MapCompositionAsset> Assets { get; set; } = new List<MapCompositionAsset>();
        public List<MapCompositionLayer> Layers { get; set; } = new List<MapCompositionLayer>();
        public List<MapCompositionOrderInterval> OrderIntervals { get; set; } = new List<MapCompositionOrderInterval>();
        public List<MapCompositionUnsupportedItem> Unsupported { get; set; } = new List<MapCompositionUnsupportedItem>();
        public List<string> ManagedFiles { get; set; } = new List<string>();
    }

    public sealed class MapCompositionMapInfo
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int? LinkedMapId { get; set; }
        public string Source { get; set; }
    }

    public sealed class MapCompositionTimeline
    {
        public int FrameRate { get; set; }
        public int DurationFrames { get; set; }
    }

    public sealed class MapCompositionRect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class MapCompositionViewport
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float Scale { get; set; } = 1f;
    }

    public sealed class MapCompositionVisibility
    {
        public int DisplayMode { get; set; }
        public bool DefaultTagVisible { get; set; } = true;
        public Dictionary<string, bool> Categories { get; set; } = new Dictionary<string, bool>();
        public Dictionary<string, bool> Tags { get; set; } = new Dictionary<string, bool>();
        public Dictionary<string, int> Quests { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> QuestEx { get; set; } = new Dictionary<string, int>();
    }

    public sealed class MapCompositionAsset
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public bool Loop { get; set; }
        public string AlphaMode { get; set; }
        public string BlendMode { get; set; }
        public string AnimationName { get; set; }
        public string SkinName { get; set; }
        public string SpineVersion { get; set; }
        public bool? SourcePremultipliedAlpha { get; set; }
        public List<MapCompositionAssetFrame> Frames { get; set; } = new List<MapCompositionAssetFrame>();
    }

    public sealed class MapCompositionAssetFrame
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int DelayMs { get; set; }
        public int DurationFrames { get; set; }
        public int OriginX { get; set; }
        public int OriginY { get; set; }
        public int A0 { get; set; }
        public int A1 { get; set; }
        public string BlendMode { get; set; }
    }

    public sealed class MapCompositionLayer
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string SemanticLayer { get; set; }
        public string AssetId { get; set; }
        public int DrawOrder { get; set; }
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public string BlendMode { get; set; }
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public bool FlipX { get; set; }
        public bool FlipY { get; set; }
        public List<MapCompositionTransformKey> TransformKeys { get; set; } = new List<MapCompositionTransformKey>();
        public MapCompositionRepeat Repeat { get; set; }

        // Source/render metadata retained for diagnostics and accurate back reconstruction.
        public string SourcePath { get; set; }
        public int SourceLayer { get; set; }
        public int SourceZ { get; set; }
        public int SourceIndex { get; set; }
        public int SourceTimeOffsetMs { get; set; }
        public string TileMode { get; set; }
        public int? TileWidth { get; set; }
        public int? TileHeight { get; set; }
        public int? Rx { get; set; }
        public int? Ry { get; set; }
        public int? ScreenMode { get; set; }
    }

    public sealed class MapCompositionRepeat
    {
        public int CountX { get; set; }
        public int CountY { get; set; }
        public int StepX { get; set; }
        public int StepY { get; set; }
    }

    public sealed class MapCompositionTransformKey
    {
        public int Frame { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Opacity { get; set; }
        public float ScaleX { get; set; }
        public float ScaleY { get; set; }
        public float Rotation { get; set; }
    }

    public sealed class MapCompositionOrderInterval
    {
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public List<string> LayerIds { get; set; } = new List<string>();
    }

    public sealed class MapCompositionUnsupportedItem
    {
        public string Path { get; set; }
        public string Kind { get; set; }
        public string Reason { get; set; }
    }
}
