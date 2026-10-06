using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WzComparerR2.AvatarCommon;
using WzComparerR2.CharaSim;
using WzComparerR2.Common;
using WzComparerR2.WzLib;
using WzUnity;

namespace WzComparerR2.Avatar.Export
{
    /// <summary>Outfit inventory is independent of whether a part produces a rendered track.</summary>
    public static class UnityAvatarEquipment
    {
        private static readonly string[] SlotNames =
        {
            "Body", "Head", "Face", "Hair", "Cap", "Coat", "Longcoat", "Pants", "Shoes", "Glove",
            "SubWeapon", "Cape", "Weapon", "Earrings", "FaceAccessory", "EyeAccessory", "Taming", "Saddle",
            "Chair", "Effect", "Pendant", "Belt", "ShoulderPad", "Pocket", "Emblem", "Ring1", "Ring2", "Ring3", "Ring4"
        };

        public static List<WzEquippedItem> Capture(AvatarCanvas avatar, StringLinker strings = null)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            var result = new List<WzEquippedItem>();
            for (int slot = 0; slot < avatar.Parts.Length; slot++)
            {
                AvatarPart part = avatar.Parts[slot];
                if (part == null) continue;
                var item = new WzEquippedItem
                {
                    slotIndex = slot,
                    slot = slot < SlotNames.Length ? SlotNames[slot] : "Slot" + slot.ToString(CultureInfo.InvariantCulture),
                    itemId = part.ID?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    sourcePath = part.Node?.FullPathToFile ?? string.Empty,
                    islot = part.ISlot ?? string.Empty, vslot = part.VSlot ?? string.Empty,
                    visible = part.Visible, effectVisible = part.EffectVisible,
                    hasImage = part.HasImage, isSkill = part.IsSkill,
                    name = string.Empty, nameSourcePath = string.Empty, illusionRingSourcePath = string.Empty
                };
                Wz_Node info = ReadInfo(part.Node);
                if (info != null) CaptureInfo(info, "info", item.metadata);
                ReadName(part.ID, part.IsSkill, strings, info, item);
                ClassifyIllusionRing(part.ID, part.IsSkill, part.Node, item);
                result.Add(item);
            }
            return result;
        }

        public static void ClassifyIllusionRing(int? itemId, bool isSkill, Wz_Node source, WzEquippedItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            item.isIllusionRing = false;
            item.illusionRingClassificationKnown = false;
            item.illusionRingSourcePath = string.Empty;
            if (!itemId.HasValue || itemId.Value <= 0 || isSkill || source == null) return;
            if (Gear.GetGearType(itemId.Value) != GearType.ring)
            {
                item.illusionRingClassificationKnown = true;
                item.illusionRingSourcePath = source.FullPathToFile;
                return;
            }

            // Forced chair/skill identities must not impersonate an original ring image.
            string filename = source.Text;
            if (!filename.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(filename.Substring(0, filename.Length - 4), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int sourceId) || sourceId != itemId.Value) return;
            Wz_Node info = ReadInfo(source);
            if (info == null) return;
            Wz_Node gradeNode = info.Nodes["illusionGrade"];
            if (gradeNode == null)
            {
                item.illusionRingClassificationKnown = true;
                item.illusionRingSourcePath = info.FullPathToFile;
                return;
            }
            gradeNode = ResolveSourceNode(gradeNode);
            if (gradeNode == null || !TryReadInteger(gradeNode.Value, out int grade)) return;
            item.illusionRingClassificationKnown = true;
            item.isIllusionRing = grade >= 0;
            item.illusionRingSourcePath = gradeNode.FullPathToFile;
            item.metadata.Add(new WzMetadata { key = "illusionRing/grade", value = grade.ToString(CultureInfo.InvariantCulture) });
        }

        private static Wz_Node ReadInfo(Wz_Node source)
        {
            Wz_Node info = ResolveSourceNode(source?.FindNodeByPath("info"));
            return info?.Value == null ? info : null;
        }

        private static Wz_Node ResolveSourceNode(Wz_Node node)
        {
            var visited = new HashSet<Wz_Node>();
            while (node?.Value is Wz_Uol uol)
            {
                if (!visited.Add(node)) return null;
                node = uol.HandleUol(node);
            }
            return node;
        }

        private static bool TryReadInteger(object value, out int result)
        {
            result = 0;
            // Do not turn missing values, fractional values, or malformed text into grade zero.
            if (!(value is sbyte || value is byte || value is short || value is ushort || value is int
                || value is uint || value is long || value is ulong || value is string)) return false;
            return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out result);
        }

        private static void ReadName(int? id, bool isSkill, StringLinker strings, Wz_Node info, WzEquippedItem item)
        {
            StringResult found = null;
            if (id.HasValue && strings != null)
            {
                if (isSkill) strings.StringSkill.TryGetValue(id.Value, out found);
                else if (!strings.StringEqp.TryGetValue(id.Value, out found))
                    strings.StringItem.TryGetValue(id.Value, out found);
            }
            if (!string.IsNullOrEmpty(found?.Name))
            {
                item.name = found.Name;
                item.nameSourcePath = found.FullPath ?? string.Empty;
            }
            else if (info?.Nodes["name"]?.Value is string name)
            {
                item.name = name;
                item.nameSourcePath = info.Nodes["name"].FullPathToFile;
            }
        }

        private static void CaptureInfo(Wz_Node node, string key, List<WzMetadata> metadata)
        {
            object value = node.Value;
            if (value is string || value is bool || value is sbyte || value is byte || value is short
                || value is ushort || value is int || value is uint || value is long || value is ulong
                || value is float || value is double || value is decimal)
                metadata.Add(new WzMetadata { key = key, value = Convert.ToString(value, CultureInfo.InvariantCulture) });
            else if (value is Wz_Vector vector)
                metadata.Add(new WzMetadata { key = key,
                    value = vector.X.ToString(CultureInfo.InvariantCulture) + "," + vector.Y.ToString(CultureInfo.InvariantCulture) });
            else if (value is Wz_Uol uol)
                metadata.Add(new WzMetadata { key = key + "/uol", value = uol.Uol });
            foreach (Wz_Node child in node.Nodes.OrderBy(child => child.Text, StringComparer.Ordinal))
                CaptureInfo(child, key + "/" + child.Text, metadata);
        }
    }
}
