using System.Collections.Generic;

namespace WzComparerR2.MapRender.Export
{
    /// <summary>Periodic motion encoded from the source map's movement parameters.</summary>
    public sealed class MapCompositionMotion
    {
        public MapCompositionMotionTracks Tracks { get; set; } = new MapCompositionMotionTracks();
    }

    public sealed class MapCompositionMotionTracks
    {
        public MapCompositionMotionTrack X { get; set; }
        public MapCompositionMotionTrack Y { get; set; }
        public MapCompositionMotionTrack Opacity { get; set; }
        public MapCompositionMotionTrack ScaleX { get; set; }

        internal bool Any => X != null || Y != null || Opacity != null || ScaleX != null;
    }

    public sealed class MapCompositionMotionTrack
    {
        public string Kind { get; set; }
        public bool Loop { get; set; } = true;
        public double CycleMs { get; set; }
        public double PhaseMs { get; set; }
        public bool PixelSnap { get; set; }
        public double Offset { get; set; }
        public double Amplitude { get; set; }
        public List<MapCompositionMotionKey> Keys { get; set; } = new List<MapCompositionMotionKey>();
    }

    public sealed class MapCompositionMotionKey
    {
        public double TimeMs { get; set; }
        public double Value { get; set; }
        public string Interpolation { get; set; }
    }

}
