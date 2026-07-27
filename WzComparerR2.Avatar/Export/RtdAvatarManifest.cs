using System.Collections.Generic;

namespace WzComparerR2.Avatar.Export
{
    internal sealed class RtdAvatarManifest
    {
        public string Schema { get; set; } = "wzcomparer-rtd-avatar";
        public int Version { get; set; } = 1;
        public int FrameRate { get; set; } = 30;
        public string Character { get; set; } = "avatar";
        public List<string> ManagedFiles { get; } = new List<string>();
        public List<RtdEffect> Effects { get; } = new List<RtdEffect>();
        public List<RtdAsset> Assets { get; } = new List<RtdAsset>();
        public List<RtdAction> Actions { get; } = new List<RtdAction>();
    }

    internal sealed class RtdEffect
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Kind { get; set; }
        public int SlotIndex { get; set; }
        public int ItemId { get; set; }
    }

    internal sealed class RtdAsset
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    internal sealed class RtdAction
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string FileToken { get; set; }
        public int CanvasWidth { get; set; }
        public int CanvasHeight { get; set; }
        public List<RtdBaseFrame> BaseFrames { get; } = new List<RtdBaseFrame>();
        public List<RtdEffectTrack> EffectTracks { get; } = new List<RtdEffectTrack>();
        public int MasterFrames { get; set; }
        public string MasterMode { get; set; }
        public List<string> Warnings { get; } = new List<string>();
        public List<RtdInterval> Intervals { get; } = new List<RtdInterval>();
    }

    internal sealed class RtdBaseFrame
    {
        public int BodyFrame { get; set; }
        public string Expression { get; set; }
        public int ExpressionIndex { get; set; }
        public string Path { get; set; }
        public int DurationFrames { get; set; }
    }

    internal sealed class RtdEffectTrack
    {
        public string EffectId { get; set; }
        public string Branch { get; set; }
        public string ResolvedAction { get; set; }
        public int CycleFrames { get; set; }
        public List<RtdEffectFrame> Frames { get; } = new List<RtdEffectFrame>();
    }

    internal sealed class RtdEffectFrame
    {
        public int SourceFrameIndex { get; set; }
        public int DelayMs { get; set; }
        public int DurationFrames { get; set; }
        public int A0 { get; set; }
        public int A1 { get; set; }
    }

    internal sealed class RtdInterval
    {
        public int StartFrame { get; set; }
        public int DurationFrames { get; set; }
        public List<RtdPlane> Planes { get; } = new List<RtdPlane>();
    }

    internal sealed class RtdPlane
    {
        public string AssetId { get; set; }
        public string Kind { get; set; }
        public string EffectId { get; set; }
        public string Branch { get; set; }
        public string SourceKey { get; set; }
        public int DrawOrder { get; set; }
        public string RawZ { get; set; }
        public int ResolvedZ { get; set; }
        public int OpacityStart { get; set; }
        public int OpacityEnd { get; set; }
    }
}
