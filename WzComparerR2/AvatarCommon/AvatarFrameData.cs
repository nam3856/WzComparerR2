using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WzComparerR2.WzLib;

namespace WzComparerR2.AvatarCommon
{
    public class AvatarFrameData
    {
        public AvatarFrameData(
            Wz_Node frameNode,
            Wz_Node mixFrameNode,
            int mixRatio,
            PrismDataCollection prismData,
            bool isBodyPart = false)
            : this(frameNode, mixFrameNode, mixRatio, prismData, null, AvatarRenderPrimitiveKind.Base, null, null, null, isBodyPart)
        {
        }

        public AvatarFrameData(
            Wz_Node frameNode,
            Wz_Node mixFrameNode,
            int mixRatio,
            PrismDataCollection prismData,
            ActionFrame actionFrame,
            AvatarRenderPrimitiveKind primitiveKind = AvatarRenderPrimitiveKind.Base,
            int? effectSlot = null,
            int? effectItemId = null,
            string effectBranch = null,
            bool isBodyPart = false)
        {
            this.FrameNode = frameNode;
            this.MixFrameNode = mixFrameNode;
            this.MixRatio = mixRatio;
            this.prismData = prismData;
            this.IsBodyPart = isBodyPart;
            this.ActionFrame = actionFrame;
            this.PrimitiveKind = primitiveKind;
            this.EffectSlot = effectSlot;
            this.EffectItemId = effectItemId;
            this.EffectBranch = effectBranch;
            this.ApplyAvatarScale = true;
        }

        public AvatarFrameData(
            Wz_Node frameNode,
            Wz_Node mixFrameNode,
            int mixRatio,
            AvatarPart part,
            Dictionary<string, string> customOriginMap = null,
            bool isBodyPart = false,
            bool applyAvatarScale = true,
            ActionFrame actionFrame = null,
            AvatarRenderPrimitiveKind primitiveKind = AvatarRenderPrimitiveKind.Base,
            int? effectSlot = null,
            int? effectItemId = null,
            string effectBranch = null)
        {
            this.FrameNode = frameNode;
            this.MixFrameNode = mixFrameNode;
            this.MixRatio = mixRatio;
            this.Part = part;
            this.customOriginMap = customOriginMap;
            this.IsBodyPart = isBodyPart;
            this.ApplyAvatarScale = applyAvatarScale;
            this.ActionFrame = actionFrame;
            this.PrimitiveKind = primitiveKind;
            this.EffectSlot = effectSlot;
            this.EffectItemId = effectItemId;
            this.EffectBranch = effectBranch;
        }

        private readonly PrismDataCollection prismData;
        private readonly Dictionary<string, string> customOriginMap;
        public AvatarPart Part { get; private set; }
        public Wz_Node FrameNode { get; private set; }
        public Wz_Node MixFrameNode { get; private set; }
        public int MixRatio { get; private set; }
        public PrismDataCollection PrismData { get { return this.Part?.PrismData ?? this.prismData ?? new PrismDataCollection(); } }
        public bool IsBodyPart { get; private set; }
        public ActionFrame ActionFrame { get; private set; }
        public AvatarRenderPrimitiveKind PrimitiveKind { get; private set; }
        public int? EffectSlot { get; private set; }
        public int? EffectItemId { get; private set; }
        public string EffectBranch { get; private set; }
        public bool ApplyAvatarScale { get; private set; }
        public Dictionary<string, string> CustomOriginMap { get { return this.Part?.CustomOriginMap ?? this.customOriginMap ?? new(); } }
    }
}
