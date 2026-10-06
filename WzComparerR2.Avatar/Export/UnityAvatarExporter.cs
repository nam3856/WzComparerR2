using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WzComparerR2.AvatarCommon;
using WzComparerR2.UnityExport;
using WzUnity;

namespace WzComparerR2.Avatar.Export
{
    /// <summary>
    /// Exports the equipped appearance as separately addressable sprite layers.
    /// Face and equipment effects retain their own clocks; body-dependent images
    /// and attachments are represented by poses instead of expanding a common LCM.
    /// </summary>
    public sealed class UnityAvatarExporter
    {
        private readonly AvatarCanvas avatar;

        public string AppearanceId { get; }

        public UnityAvatarExporter(AvatarCanvas source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            // A separate skin cache is essential: preview bitmaps belong to the UI.
            avatar = Snapshot(source);
            var description = new WzEntity();
            DescribeOutfit(description);
            string canonical = string.Concat(description.metadata.OrderBy(item => item.key, StringComparer.Ordinal)
                .Select(item => item.key.Length + ":" + item.key + item.value.Length + ":" + item.value));
            using (var sha = SHA256.Create())
                AppearanceId = "avatar-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", "").ToLowerInvariant().Substring(0, 24);
        }

        public WzUnityManifest Export(string outputDirectory, IEnumerable<string> actionNames,
            CancellationToken cancellationToken = default,
            System.Action<int, int, string> reportProgress = null)
        {
            if (avatar.Body == null || avatar.Head == null)
                throw new InvalidOperationException("몸과 머리가 있는 캐릭터를 먼저 구성하세요.");
            string[] names = actionNames?.Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
            if (names.Length == 0) throw new InvalidOperationException("내보낼 동작이 없습니다.");

            using (var writer = new UnityExportWriter(outputDirectory, AppearanceId, "avatar",
                avatar.Body.Node.FullPathToFile, cancellationToken))
            {
                var entity = new WzEntity
                {
                    id = AppearanceId, kind = "avatar", displayName = "현재 캐릭터",
                    sourcePath = avatar.Body.Node.FullPathToFile
                };
                DescribeOutfit(entity);
                writer.Manifest.entities.Add(entity);
                if ((avatar.Taming?.Visible ?? false) || (avatar.Chair?.Visible ?? false))
                    writer.Warn(entity.sourcePath, "의자/라이딩의 별도 포즈와 프레임은 캐릭터 내보내기에 포함되지 않습니다.");
                try
                {
                    for (int index = 0; index < names.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string name = names[index];
                        reportProgress?.Invoke(index, names.Length, name + " 부위/표정/이펙트 내보내는 중...");
                        try
                        {
                            WzAnimationClip clip = ExportAction(writer, name, cancellationToken);
                            if (clip != null) entity.clips.Add(clip);
                        }
                        finally
                        {
                            avatar.ClearSkinCache();
                        }
                        reportProgress?.Invoke(index + 1, names.Length, name + " 완료");
                    }
                    if (entity.clips.Count == 0)
                        throw new InvalidDataException("내보낼 수 있는 캐릭터 동작을 찾지 못했습니다.");
                    entity.defaultAction = entity.clips.Any(clip => clip.name == "stand1")
                        ? "stand1" : entity.clips[0].name;
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.Commit();
                    return writer.Manifest;
                }
                finally
                {
                    avatar.ClearSkinCache();
                }
            }
        }

        private WzAnimationClip ExportAction(UnityExportWriter writer, string actionName, CancellationToken token)
        {
            ActionFrame[] body = avatar.GetActionFrames(actionName);
            if (body.Length == 0)
            {
                writer.Warn(avatar.Body.Node.FullPathToFile + "/" + actionName, "몸 동작 프레임이 없습니다.");
                return null;
            }
            string emotion = string.IsNullOrEmpty(avatar.EmotionName) ? "default" : avatar.EmotionName;
            ActionFrame[] face = avatar.GetFaceFrames(emotion);
            if (face.Length == 0) face = avatar.GetFaceFrames("default");
            if (face.Length == 0) face = new[] { new ActionFrame("default", 0) };
            WarnInvalidDelays(writer, body, avatar.Body.Node.FullPathToFile + "/" + actionName);
            WarnInvalidDelays(writer, face, avatar.Face?.Node.FullPathToFile + "/" + emotion);

            var clip = new WzAnimationClip
            {
                name = actionName,
                loop = !actionName.Equals("dead", StringComparison.OrdinalIgnoreCase)
                    && !actionName.StartsWith("die", StringComparison.OrdinalIgnoreCase)
            };
            var bodyClock = new WzAnimationTrack { id = "body", kind = "clock", slot = "body" };
            foreach (ActionFrame frame in body)
                bodyClock.frames.Add(new WzSpriteFrame { delayMs = Duration(frame), visible = false });
            clip.tracks.Add(bodyClock);
            clip.durationMs = bodyClock.frames.Sum(frame => frame.delayMs);
            Meta(bodyClock.metadata, "sourceDelaysMs", string.Join(",", body.Select(frame => frame.Delay.ToString(CultureInfo.InvariantCulture))));

            var baseStates = new List<Dictionary<string, CapturedPrimitive>>();
            for (int b = 0; b < body.Length; b++)
            {
                token.ThrowIfCancellationRequested();
                baseStates.Add(Capture(writer, body[b], face[0], null,
                    primitive => primitive.Kind == AvatarRenderPrimitiveKind.Base && !IsFace(primitive)));
            }
            AddBodyTracks(clip, baseStates, body);

            AddIndependentTracks(writer, clip, "face", face, body, face[0], -1, token);
            for (int slot = 0; slot < AvatarCanvas.LayerSlotLength; slot++)
            {
                token.ThrowIfCancellationRequested();
                if (!CanExportEffect(slot)) continue;
                ActionFrame[] frames = avatar.GetEffectFrames(actionName, slot);
                if (frames.Length > 0)
                {
                    WarnInvalidDelays(writer, frames, avatar.Body.Node.FullPathToFile + "/" + actionName + "/effect-" + slot);
                    AddIndependentTracks(writer, clip, "effect-" + slot, frames, body, face[0], slot, token);
                }
            }
            if (!clip.tracks.Any(track => track.kind != "clock"))
            {
                writer.Warn(avatar.Body.Node.FullPathToFile + "/" + actionName, "표시할 부위 이미지가 없습니다.");
                return null;
            }
            foreach (var track in clip.tracks) track.loop = clip.loop;
            return clip;
        }

        private bool CanExportEffect(int slot)
        {
            if (slot == 16 || slot == 17 || slot == AvatarCanvas.IndexChairLayer1
                || slot == AvatarCanvas.IndexChairLayer2 || slot == AvatarCanvas.IndexChairEffectLayer1
                || slot == AvatarCanvas.IndexChairEffectLayer2)
                return false;
            int owner = slot == AvatarCanvas.IndexEffectLayer2 ? AvatarCanvas.IndexEffectLayer1 : slot;
            return owner < AvatarCanvas.PartLength && avatar.Parts[owner]?.EffectNode != null
                && avatar.IsPartEffectVisible(slot);
        }

        private void AddBodyTracks(WzAnimationClip clip,
            List<Dictionary<string, CapturedPrimitive>> states, ActionFrame[] body)
        {
            foreach (string key in Keys(states))
            {
                CapturedPrimitive first = states.First(state => state.ContainsKey(key))[key];
                WzAnimationTrack track = NewTrack("part/" + key, "body", first);
                for (int index = 0; index < states.Count; index++)
                {
                    states[index].TryGetValue(key, out CapturedPrimitive primitive);
                    track.frames.Add(Frame(primitive, Duration(body[index]), body[index]));
                    AddAnchors(track, primitive, "frame/" + index);
                }
                clip.tracks.Add(track);
            }
        }

        private void AddIndependentTracks(UnityExportWriter writer, WzAnimationClip clip, string prefix,
            ActionFrame[] frames, ActionFrame[] body, ActionFrame defaultFace, int effectSlot,
            CancellationToken token)
        {
            var poses = new List<Dictionary<string, CapturedPrimitive>>();
            for (int b = 0; b < body.Length; b++)
            {
                for (int f = 0; f < frames.Length; f++)
                {
                    token.ThrowIfCancellationRequested();
                    ActionFrame[] effects = null;
                    if (effectSlot >= 0)
                    {
                        effects = new ActionFrame[AvatarCanvas.LayerSlotLength];
                        effects[effectSlot] = frames[f];
                    }
                    poses.Add(Capture(writer, body[b], effectSlot < 0 ? frames[f] : defaultFace, effects,
                        primitive => effectSlot < 0 ? IsFace(primitive)
                            : primitive.Kind == AvatarRenderPrimitiveKind.IndependentEffect
                                && primitive.EffectSlot == effectSlot));
                }
            }
            foreach (string key in Keys(poses))
            {
                CapturedPrimitive first = poses.First(pose => pose.ContainsKey(key))[key];
                WzAnimationTrack track = NewTrack(prefix + "/" + key, effectSlot < 0 ? "face" : "effect", first);
                track.poseTrack = "body";
                Meta(track.metadata, "sourceDelaysMs", string.Join(",", frames.Select(frame => frame.Delay.ToString(CultureInfo.InvariantCulture))));
                for (int f = 0; f < frames.Length; f++)
                {
                    // Pose overrides provide actual sprites and visibility. The clock
                    // frame stays visible so a hidden first body pose cannot hide all others.
                    poses[f].TryGetValue(key, out CapturedPrimitive primitive);
                    WzSpriteFrame frame = Frame(primitive ?? first, Duration(frames[f]), frames[f]);
                    frame.visible = true;
                    track.frames.Add(frame);
                }
                for (int b = 0; b < body.Length; b++)
                {
                    for (int f = 0; f < frames.Length; f++)
                    {
                        poses[b * frames.Length + f].TryGetValue(key, out CapturedPrimitive primitive);
                        track.poses.Add(new WzTrackPose
                        {
                            poseFrame = b, frameIndex = f, overrideSprite = true,
                            assetId = primitive?.AssetId,
                            x = primitive?.X ?? 0, y = primitive?.Y ?? 0,
                            z = primitive?.Z ?? 0, drawOrder = primitive?.DrawOrder ?? 0,
                            visible = primitive != null
                        });
                        AddAnchors(track, primitive, "pose/" + b + "/" + f);
                    }
                }
                clip.tracks.Add(track);
            }
        }

        private Dictionary<string, CapturedPrimitive> Capture(UnityExportWriter writer, ActionFrame body,
            ActionFrame face, ActionFrame[] effects, Func<AvatarRenderPrimitive, bool> include)
        {
            var result = new Dictionary<string, CapturedPrimitive>(StringComparer.Ordinal);
            AvatarRenderPrimitive[] primitives = avatar.CreateFramePrimitives(
                avatar.CreateFrame(body, face, null, effects ?? new ActionFrame[AvatarCanvas.LayerSlotLength]));
            var duplicateKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                foreach (AvatarRenderPrimitive primitive in primitives)
                {
                    if (!include(primitive)) continue;
                    string skinName = primitive.SkinName ?? "sprite";
                    // Direct PNG effects use numeric source frame names; their
                    // renderer identity must remain stable while images change.
                    if (primitive.Kind == AvatarRenderPrimitiveKind.IndependentEffect
                        && int.TryParse(skinName, out _)) skinName = "effect";
                    string stem = primitive.PartSlot + "/" + skinName;
                    duplicateKeys.TryGetValue(stem, out int duplicate);
                    duplicateKeys[stem] = duplicate + 1;
                    string key = stem + "/" + duplicate;
                    result.Add(key, new CapturedPrimitive
                    {
                        AssetId = writer.AddBitmap(primitive.Bitmap),
                        X = primitive.Position.X, Y = primitive.Position.Y,
                        Z = -primitive.ResolvedZ, DrawOrder = primitive.DrawOrdinal,
                        Slot = primitive.PartSlot, ItemId = primitive.PartItemId,
                        Source = primitive.SourceKey, Skin = skinName, RawZ = primitive.RawZ,
                        Bone = primitive.BoneName, BonePosition = primitive.BonePosition,
                        Anchors = primitive.SourceAnchors
                    });
                }
            }
            finally
            {
                foreach (Bitmap bitmap in primitives.Where(primitive => primitive.OwnsBitmap)
                    .Select(primitive => primitive.Bitmap).Distinct()) bitmap.Dispose();
            }
            return result;
        }

        private static bool IsFace(AvatarRenderPrimitive primitive)
        {
            return primitive.Kind == AvatarRenderPrimitiveKind.Base
                && (primitive.PartSlot == 2 || primitive.PartSlot == 14);
        }

        private static IEnumerable<string> Keys(IEnumerable<Dictionary<string, CapturedPrimitive>> states)
        {
            return states.SelectMany(state => state.Keys).Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal);
        }

        private static WzAnimationTrack NewTrack(string id, string kind, CapturedPrimitive primitive)
        {
            var track = new WzAnimationTrack
            {
                id = id, kind = kind, slot = primitive.Slot.ToString(CultureInfo.InvariantCulture),
                itemId = primitive.ItemId?.ToString(CultureInfo.InvariantCulture)
            };
            Meta(track.metadata, "skin", primitive.Skin);
            Meta(track.metadata, "rawZ", primitive.RawZ);
            return track;
        }

        private static WzSpriteFrame Frame(CapturedPrimitive primitive, double duration, ActionFrame source)
        {
            return new WzSpriteFrame
            {
                assetId = primitive?.AssetId, delayMs = duration,
                x = primitive?.X ?? 0, y = primitive?.Y ?? 0,
                z = primitive?.Z ?? 0, drawOrder = primitive?.DrawOrder ?? 0,
                visible = primitive != null, sourcePath = primitive?.Source,
                a0 = Math.Max(0, Math.Min(255, source.A0)),
                a1 = Math.Max(0, Math.Min(255, source.A1))
            };
        }

        private static double Duration(ActionFrame frame)
        {
            return frame.Delay == 0 ? 120 : Math.Abs((long)frame.Delay);
        }

        private static void WarnInvalidDelays(UnityExportWriter writer, ActionFrame[] frames, string path)
        {
            for (int index = 0; index < frames.Length; index++)
            {
                // A static default face has no authored frame/delay property.
                if (frames[index].Delay == 0 && frames[index].Frame.HasValue)
                    writer.Warn(path + "/" + index, "프레임 delay가 0이므로 WzComparer 기본값 120ms를 사용했습니다.");
            }
        }

        private static void AddAnchors(WzAnimationTrack track, CapturedPrimitive primitive, string prefix)
        {
            if (primitive == null) return;
            Meta(track.metadata, prefix + "/source", primitive.Source);
            Meta(track.metadata, prefix + "/bone", primitive.Bone);
            Meta(track.metadata, prefix + "/bonePosition", Coordinates(primitive.BonePosition));
            if (primitive.Anchors != null)
                foreach (KeyValuePair<string, Point> anchor in primitive.Anchors.OrderBy(item => item.Key, StringComparer.Ordinal))
                    Meta(track.metadata, prefix + "/map/" + anchor.Key, Coordinates(anchor.Value));
        }

        private void DescribeOutfit(WzEntity entity)
        {
            Meta(entity.metadata, "equipmentScope", "current-outfit");
            Meta(entity.metadata, "maskingScope", "current-combination");
            Meta(entity.metadata, "capType", avatar.CapType);
            Meta(entity.metadata, "emotion", avatar.EmotionName ?? "default");
            Meta(entity.metadata, "earType", avatar.EarType);
            Meta(entity.metadata, "weaponType", avatar.WeaponType);
            Meta(entity.metadata, "weaponIndex", avatar.WeaponIndex);
            Meta(entity.metadata, "hairCover", avatar.HairCover);
            Meta(entity.metadata, "hideBody", avatar.HideBody);
            Meta(entity.metadata, "showWeaponEffect", avatar.ShowWeaponEffect);
            Meta(entity.metadata, "showWeaponJumpEffect", avatar.ShowWeaponJumpEffect);
            Meta(entity.metadata, "showHairShade", avatar.ShowHairShade);
            Meta(entity.metadata, "applyBodyRelativeMove", avatar.ApplyBRM);
            Meta(entity.metadata, "groupChair", avatar.GroupChair);
            Meta(entity.metadata, "avatarScale", avatar.fAvatarScale);
            foreach (KeyValuePair<string, Point> origin in avatar.CustomOrigin.OrderBy(item => item.Key, StringComparer.Ordinal))
                Meta(entity.metadata, "customOrigin/" + origin.Key, Coordinates(origin.Value));
            for (int slot = 0; slot < avatar.Parts.Length; slot++)
            {
                AvatarPart part = avatar.Parts[slot];
                if (part == null) continue;
                string key = "parts/" + slot + "/";
                Meta(entity.metadata, key + "itemId", part.ID);
                Meta(entity.metadata, key + "source", part.Node.FullPathToFile);
                Meta(entity.metadata, key + "islot", part.ISlot);
                Meta(entity.metadata, key + "vslot", part.VSlot);
                Meta(entity.metadata, key + "visible", part.Visible);
                Meta(entity.metadata, key + "effectVisible", part.EffectVisible);
                Meta(entity.metadata, key + "mixColor", part.MixColor);
                Meta(entity.metadata, key + "mixOpacity", part.MixOpacity);
                foreach (var origin in part.CustomOriginMap.OrderBy(item => item.Key, StringComparer.Ordinal))
                    Meta(entity.metadata, key + "customOriginMap/" + origin.Key, origin.Value);
                foreach (PrismDataCollection.PrismDataType type in Enum.GetValues(typeof(PrismDataCollection.PrismDataType)))
                {
                    PrismData prism = part.PrismData.Get(type);
                    string prismKey = key + "prism/" + type + "/";
                    Meta(entity.metadata, prismKey + "type", prism.Type);
                    Meta(entity.metadata, prismKey + "hue", prism.Hue);
                    Meta(entity.metadata, prismKey + "saturation", prism.Saturation);
                    Meta(entity.metadata, prismKey + "brightness", prism.Brightness);
                    Meta(entity.metadata, prismKey + "convertPureBlack", prism.ConvertPureBlack);
                }
            }
        }

        private static string Coordinates(Point point)
        {
            return point.X.ToString(CultureInfo.InvariantCulture) + "," + point.Y.ToString(CultureInfo.InvariantCulture);
        }

        private static void Meta(List<WzMetadata> metadata, string key, object value)
        {
            metadata.Add(new WzMetadata { key = key, value = Convert.ToString(value, CultureInfo.InvariantCulture) });
        }

        private static AvatarCanvas Snapshot(AvatarCanvas source)
        {
            var snapshot = new AvatarCanvas
            {
                ActionName = source.ActionName, EmotionName = source.EmotionName,
                TamingActionName = source.TamingActionName, HairCover = source.HairCover,
                ShowHairShade = source.ShowHairShade, ShowWeaponEffect = source.ShowWeaponEffect,
                ShowWeaponJumpEffect = source.ShowWeaponJumpEffect, HideBody = source.HideBody,
                ApplyBRM = source.ApplyBRM, WeaponIndex = source.WeaponIndex,
                WeaponType = source.WeaponType, EarType = source.EarType,
                CapType = source.CapType, GroupChair = source.GroupChair,
                CustomOrigin = new Dictionary<string, Point>(source.CustomOrigin)
            };
            Array.Copy(source.Parts, snapshot.Parts, source.Parts.Length);
            snapshot.ZMap.AddRange(source.ZMap);
            snapshot.Actions.AddRange(source.Actions);
            snapshot.Emotions.AddRange(source.Emotions);
            snapshot.TamingActions.AddRange(source.TamingActions);
            for (int slot = 0; slot < source.EffectActions.Length; slot++)
            {
                snapshot.EffectActions[slot].AddRange(source.EffectActions[slot]);
                snapshot.EffectVisibles[slot] = source.EffectVisibles[slot];
            }
            return snapshot;
        }

        private sealed class CapturedPrimitive
        {
            public string AssetId;
            public int X, Y, Z, DrawOrder, Slot;
            public int? ItemId;
            public string Source, Skin, RawZ, Bone;
            public Point BonePosition;
            public IReadOnlyDictionary<string, Point> Anchors;
        }
    }
}
