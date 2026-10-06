using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using WzComparerR2.WzLib;

namespace WzComparerR2.AvatarCommon
{
    public class Skin
    {
        public string Name { get; set; }
        public int PartSlot { get; set; } = -1;
        public int? PartItemId { get; set; }
        public Dictionary<string, Point> SourceAnchors { get; set; } = new Dictionary<string, Point>();
        public BitmapOrigin Image { get; set; }
        public Point Offset { get; set; }
        public string Z { get; set; }
        public int ZIndex { get; set; }
        public AvatarRenderPrimitiveKind PrimitiveKind { get; set; }
        public int? EffectSlot { get; set; }
        public int? EffectItemId { get; set; }
        public string EffectBranch { get; set; }
        public string SourceKey { get; set; }
        public int A0 { get; set; } = 255;
        public int A1 { get; set; } = 255;
    }
}
