using System.Drawing;

namespace WzComparerR2.AvatarCommon
{
    public enum AvatarRenderPrimitiveKind
    {
        Base = 0,
        IndependentEffect = 1
    }

    /// <summary>
    /// A single, transformed avatar bitmap in final back-to-front draw order.
    /// </summary>
    public sealed class AvatarRenderPrimitive
    {
        public Bitmap Bitmap { get; internal set; }
        /// <summary>
        /// Gets whether <see cref="Bitmap"/> is a transformed bitmap owned by this result.
        /// Callers should dispose only owned bitmaps; shared skin-cache bitmaps remain owned by the canvas.
        /// </summary>
        public bool OwnsBitmap { get; internal set; }
        public Point Position { get; internal set; }
        public string RawZ { get; internal set; }
        public int? RawZIndex { get; internal set; }
        public int ResolvedZ { get; internal set; }
        public int DrawOrdinal { get; internal set; }
        public AvatarRenderPrimitiveKind Kind { get; internal set; }
        public int? EffectSlot { get; internal set; }
        public int? EffectItemId { get; internal set; }
        public string EffectBranch { get; internal set; }
        public string SourceKey { get; internal set; }
        public int A0 { get; internal set; }
        public int A1 { get; internal set; }
    }
}
