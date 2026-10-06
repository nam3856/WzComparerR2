using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using WzComparerR2.Common;
using WzComparerR2.WzLib;
using WzUnity;

namespace WzComparerR2.UnityExport
{
    public static class UnityEntityExporter
    {
        // The MapRender plugin supplies its existing GPU Spine baker without a reverse project reference.
        public static Func<Wz_Node, UnityExportWriter, GlobalFindNodeFunction, GraphicsDevice, List<WzAnimationClip>> SpecialAnimationExporter { get; set; }

        public static WzEntity AddEntity(Wz_Node node, string kind, UnityExportWriter writer,
            GlobalFindNodeFunction findNode, string id = null, GraphicsDevice graphicsDevice = null)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var original = Extract(node);
            string number = original.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? original.Text.Substring(0, original.Text.Length - 4) : original.Text;
            string entityId = id ?? kind.ToLowerInvariant() + "-" + number;
            var existing = writer.Manifest.entities.FirstOrDefault(e => e.id == entityId);
            if (existing != null) return existing;
            node = ResolveImageLink(original, kind, findNode);
            var entity = new WzEntity { id = entityId, kind = kind.ToLowerInvariant(), sourcePath = original.FullPathToFile, displayName = number };
            var actionNodes = new List<KeyValuePair<string, Wz_Node>>();
            foreach (var child in node.Nodes)
            {
                if (child.Text == "info") continue;
                if (child.Text.StartsWith("condition", StringComparison.Ordinal))
                {
                    foreach (var action in child.Nodes)
                        if (action.Text != "dateStart" && action.Text != "dateEnd") actionNodes.Add(new KeyValuePair<string, Wz_Node>(child.Text + "/" + action.Text, action));
                }
                else
                {
                    actionNodes.Add(new KeyValuePair<string, Wz_Node>(child.Text, child));
                    if (kind.Equals("reactor", StringComparison.OrdinalIgnoreCase) && child.Nodes["hit"] != null)
                        actionNodes.Add(new KeyValuePair<string, Wz_Node>(child.Text + "/hit", child.Nodes["hit"]));
                }
            }
            foreach (var pair in actionNodes)
            {
                writer.CancellationToken.ThrowIfCancellationRequested();
                var action = ResolveUol(pair.Value, findNode);
                if (action == null) throw new InvalidDataException("Unresolved action: " + pair.Value.FullPathToFile);
                bool loop = IsLooping(pair.Key, action);
                var track = ReadTrack(action, "body", writer, findNode, loop);
                var actionClips = new List<WzAnimationClip>();
                if (track.frames.Count == 0)
                {
                    if (IsSpecialAnimation(action))
                    {
                        var baked = SpecialAnimationExporter?.Invoke(action, writer, findNode, graphicsDevice);
                        if (baked != null && baked.Count > 0)
                        {
                            foreach (var bakedClip in baked)
                            {
                                bakedClip.name = pair.Key + "/" + bakedClip.name;
                                bakedClip.loop = loop;
                                foreach (var bakedTrack in bakedClip.tracks) bakedTrack.loop = loop;
                                actionClips.Add(bakedClip);
                            }
                        }
                        else writer.Warn(action.FullPathToFile, "Spine/전용 셰이더 애니메이션을 추출할 수 없습니다. GPU 베이커 또는 지원 형식을 확인하세요.");
                    }
                }
                else
                {
                    var clip = new WzAnimationClip { name = pair.Key, loop = loop, durationMs = track.frames.Sum(f => f.delayMs) };
                    clip.tracks.Add(track);
                    actionClips.Add(clip);
                }
                if (actionClips.Count > 0)
                    entity.clips.AddRange(AttachEffects(actionClips, action, writer, findNode, graphicsDevice, loop));
            }
            if (node.FindNodeByPath("info\\component")?.Nodes.Count > 0)
                writer.Warn(node.FullPathToFile, "장비 조합형 NPC입니다. 아바타 창에서 코디로 구성해 캐릭터 Unity 추출을 사용하세요.");
            if (entity.clips.Count == 0)
            {
                writer.Warn(original.FullPathToFile, "지원되는 애니메이션이 없습니다. 배치 정보만 유지됩니다.");
                return null;
            }
            entity.defaultAction = new[] { "stand", "fly", "move", "0" }.FirstOrDefault(name => entity.clips.Any(c => c.name == name)) ?? entity.clips[0].name;
            writer.Manifest.entities.Add(entity);
            return entity;
        }

        public static WzAnimationTrack ReadTrack(Wz_Node action, string id, UnityExportWriter writer, GlobalFindNodeFunction findNode, bool loop = true)
        {
            var track = new WzAnimationTrack { id = id, kind = id, loop = loop };
            action = ResolveUol(action, findNode);
            if (action == null) return track;
            if (action.Value is Wz_Png)
            {
                track.frames.Add(ReadFrame(action, writer, findNode));
                return track;
            }
            var numeric = action.Nodes.Where(n => int.TryParse(n.Text, out _)).OrderBy(n => int.Parse(n.Text)).ToArray();
            var resolved = numeric.Select(node => ResolveUol(node, findNode)).ToArray();
            bool hasPng = resolved.Any(node => node?.Value is Wz_Png);
            if (!hasPng && numeric.Length > 0 && numeric[0].Text == "0" && resolved[0]?.Value == null && resolved[0]?.Nodes["0"] != null)
                return ReadTrack(resolved[0], id, writer, findNode, loop);
            bool expectedFrames = hasPng || numeric.Where((node, index) =>
                node.Value is Wz_Uol && resolved[index] == null ||
                resolved[index]?.Nodes["origin"] != null || resolved[index]?.Nodes["delay"] != null).Any();
            if (!expectedFrames) return track; // Numeric event/condition metadata is not an animation sequence.
            // Validate the complete sequence before decoding any pixels. A broken first frame must not
            // disappear while later frames are exported as a shorter, apparently successful animation.
            for (int index = 0; index < numeric.Length; index++)
                if (!(resolved[index]?.Value is Wz_Png))
                    throw new InvalidDataException("Animation contains a missing or unresolved frame: " + numeric[index].FullPathToFile);
            // Real WZ actions can author sparse indices (Npc/1012102.img/act2156 omits 19).
            // Preserve every existing frame in numeric order; an absent index is not a broken link.
            if (numeric.Where((node, index) => int.Parse(node.Text) != index).Any())
                writer.Warn(action.FullPathToFile, "프레임 번호에 빈 구간이 있어 존재하는 프레임을 번호 순서대로 유지했습니다.");
            foreach (var frame in resolved)
            {
                writer.CancellationToken.ThrowIfCancellationRequested();
                track.frames.Add(ReadFrame(frame, writer, findNode));
            }
            if (action.Nodes["zigzag"].GetValueEx(0) != 0 && track.frames.Count > 2)
                track.frames.AddRange(track.frames.Skip(1).Take(track.frames.Count - 2).Reverse().ToArray());
            return track;
        }

        private static IEnumerable<WzAnimationClip> AttachEffects(List<WzAnimationClip> bodyClips, Wz_Node action,
            UnityExportWriter writer, GlobalFindNodeFunction findNode, GraphicsDevice graphicsDevice, bool loop)
        {
            var effectNode = action.FindNodeByPath("info\\effect");
            if (effectNode == null) return bodyClips;
            var resolvedEffect = ResolveUol(effectNode, findNode);
            if (resolvedEffect == null) throw new InvalidDataException("Unresolved action effect: " + effectNode.FullPathToFile);
            var effects = new List<WzAnimationClip>();
            var effectTrack = ReadTrack(resolvedEffect, "effect", writer, findNode, loop);
            if (effectTrack.frames.Count > 0)
                effects.Add(new WzAnimationClip { name = "effect", tracks = new List<WzAnimationTrack> { effectTrack } });
            else if (IsSpecialAnimation(resolvedEffect))
                effects.AddRange(SpecialAnimationExporter?.Invoke(resolvedEffect, writer, findNode, graphicsDevice) ?? new List<WzAnimationClip>());
            if (effects.Count == 0)
            {
                writer.Warn(effectNode.FullPathToFile, "이 동작의 이펙트를 PNG 프레임으로 변환할 수 없습니다.");
                return bodyClips;
            }
            double startMs = Math.Max(0, action.FindNodeByPath("info\\effectAfter").GetValueEx(0));
            foreach (var effect in effects)
            {
                for (int index = 0; index < effect.tracks.Count; index++)
                {
                    var track = effect.tracks[index];
                    track.id = "effect/" + index + "/" + track.id;
                    track.kind = "effect";
                    track.loop = loop;
                    track.startMs += startMs;
                    foreach (var frame in track.frames) frame.drawOrder += 1;
                }
            }
            var result = new List<WzAnimationClip>();
            foreach (var body in bodyClips)
                foreach (var effect in effects)
                {
                    // Preserve every supported Spine variant as a selectable action. Selecting only
                    // the first effect animation would discard valid source motions.
                    var combined = new WzAnimationClip { name = body.name + (effects.Count > 1 ? "/effect/" + effect.name : ""), loop = body.loop };
                    combined.tracks.AddRange(body.tracks);
                    combined.tracks.AddRange(effect.tracks);
                    combined.durationMs = Math.Max(body.durationMs, combined.tracks.Max(track => track.startMs + track.frames.Sum(frame => frame.delayMs)));
                    result.Add(combined);
                }
            return result;
        }

        private static WzSpriteFrame ReadFrame(Wz_Node frameNode, UnityExportWriter writer, GlobalFindNodeFunction findNode)
        {
            var source = frameNode;
            var seen = new HashSet<Wz_Node>();
            while (true)
            {
                if (!seen.Add(source)) throw new InvalidDataException("Circular PNG link: " + frameNode.FullPathToFile);
                var next = ResolveUol(source.GetLinkedSourceNode(findNode), findNode);
                if (next == null) throw new InvalidDataException("Missing PNG link: " + source.FullPathToFile);
                if (ReferenceEquals(source, next)) break;
                source = next;
            }
            if (!(source.Value is Wz_Png png)) throw new InvalidDataException("Expected PNG: " + source.FullPathToFile);
            string assetId;
            using (var bitmap = png.ExtractPng()) assetId = writer.AddBitmap(bitmap);
            var origin = frameNode.Nodes["origin"].GetValueEx<Wz_Vector>(null);
            int a0 = frameNode.Nodes["a0"].GetValueEx(255);
            int delay = frameNode.Nodes["delay"].GetValueEx(120);
            if (delay == 0) { delay = 120; writer.Warn(frameNode.FullPathToFile, "0ms 프레임 지연을 렌더러 기본값 120ms로 정규화했습니다."); }
            int blend = frameNode.Nodes["blend"].GetValueEx(0);
            // Match the native MapRender ResourceLoader.LoadFrame life renderer: missing a1 = a0.
            // WZ blend controls alpha interpolation, not an additive material.
            return new WzSpriteFrame
            {
                assetId = assetId, delayMs = Math.Abs((double)delay), originX = origin?.X ?? 0, originY = origin?.Y ?? 0,
                z = frameNode.Nodes["z"].GetValueEx(0), a0 = Math.Max(0, Math.Min(255, a0)),
                a1 = Math.Max(0, Math.Min(255, frameNode.Nodes["a1"].GetValueEx(a0))),
                sourcePath = frameNode.FullPathToFile, blend = blend != 0 ? "interpolate" : "normal"
            };
        }

        private static bool IsLooping(string name, Wz_Node action)
        {
            if (action.Nodes["repeat"] != null) return action.Nodes["repeat"].GetValueEx(0) != 0;
            string leaf = name.Split('/').Last();
            return !(leaf.StartsWith("die", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("hit", StringComparison.OrdinalIgnoreCase) ||
                leaf.StartsWith("attack", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("skill", StringComparison.OrdinalIgnoreCase) ||
                leaf.StartsWith("regen", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSpecialAnimation(Wz_Node node, int depth = 0) => node != null && depth <= 4 &&
            (node.Nodes["spine"] != null || node.Nodes["type"].GetValueEx<string>(null) == "sprite" ||
             node.Text.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase) || node.Text.EndsWith(".skel", StringComparison.OrdinalIgnoreCase) ||
             node.Value == null && node.Nodes.Any(child => child.Text != "info" && IsSpecialAnimation(child, depth + 1)));

        private static Wz_Node ResolveImageLink(Wz_Node node, string kind, GlobalFindNodeFunction findNode)
        {
            var seen = new HashSet<Wz_Node>();
            while (true)
            {
                if (!seen.Add(node)) throw new InvalidDataException("Circular info/link: " + node.FullPathToFile);
                var link = node.FindNodeByPath("info\\link");
                if (link?.Value == null) return node;
                if (!int.TryParse(Convert.ToString(link.Value), out int number)) throw new InvalidDataException("Invalid info/link: " + link.FullPathToFile);
                string category = kind.Equals("mob", StringComparison.OrdinalIgnoreCase) ? "Mob" : kind.Equals("npc", StringComparison.OrdinalIgnoreCase) ? "Npc" : "Reactor";
                node = findNode?.Invoke(category + "\\" + number.ToString("D7") + ".img", null);
                if (node == null) throw new InvalidDataException("Missing linked " + category + ": " + number);
                node = Extract(node);
            }
        }

        private static Wz_Node Extract(Wz_Node node)
        {
            if (node.Value is Wz_Image img)
            {
                if (!img.TryExtract(out var error)) throw new InvalidDataException("Cannot extract " + node.FullPathToFile, error);
                return img.Node;
            }
            return node;
        }

        public static Wz_Node ResolveUol(Wz_Node node, GlobalFindNodeFunction findNode)
        {
            var seen = new HashSet<Wz_Node>();
            while (node?.Value is Wz_Uol uol)
            {
                if (!seen.Add(node)) throw new InvalidDataException("Circular UOL: " + node.FullPathToFile);
                node = uol.Uol.StartsWith("/", StringComparison.Ordinal) ? findNode?.Invoke(uol.Uol.TrimStart('/'), null) : uol.HandleUol(node);
            }
            return node;
        }
    }
}
