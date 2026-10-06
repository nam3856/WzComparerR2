using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using WzComparerR2.AvatarCommon;
using WzComparerR2.PluginBase;
using WzComparerR2.UnityExport;
using WzComparerR2.WzLib;
using WzUnity;

namespace WzComparerR2.Avatar.Export
{
    /// <summary>Exports the original complete-body sprites of an equipped illusion ring.</summary>
    internal static class UnityAvatarIllusionExporter
    {
        internal static WzUnityManifest Export(AvatarPart ring, WzEquippedItem equipped, WzEntity entity,
            string outputDirectory, IEnumerable<string> actionNames, CancellationToken token,
            System.Action<int, int, string> reportProgress)
        {
            string[] names = actionNames.ToArray();
            using (var writer = new UnityExportWriter(outputDirectory, entity.id, "avatar", entity.sourcePath, token))
            {
                for (int index = 0; index < names.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    string name = names[index];
                    reportProgress?.Invoke(index, names.Length, name + " 원본 환상 반지 내보내는 중...");
                    string sourceAction = name == "stand2" && ring.Node.Nodes["stand2"] == null ? "stand1" : name;
                    Wz_Node action = UnityEntityExporter.ResolveUol(ring.Node.Nodes[sourceAction], PluginManager.FindWz);
                    if (action == null)
                    {
                        writer.Warn(ring.Node.FullPathToFile + "/" + name, "환상 반지에 원본 동작이 없습니다.");
                        continue;
                    }
                    bool loop = action.Nodes["repeat"] == null || ReadInteger(action.Nodes["repeat"], "repeat") != 0;
                    var track = UnityEntityExporter.ReadTrack(action, "illusion-ring/" + equipped.slotIndex,
                        writer, PluginManager.FindWz, loop);
                    if (track.frames.Count == 0)
                    {
                        writer.Warn(action.FullPathToFile, "환상 반지에 표시할 원본 프레임이 없습니다.");
                        continue;
                    }
                    track.kind = "body";
                    track.slot = equipped.slot;
                    track.itemId = equipped.itemId;
                    track.metadata.Add(new WzMetadata { key = "source", value = action.FullPathToFile });
                    track.metadata.Add(new WzMetadata { key = "sourceAction", value = sourceAction });
                    Wz_Node parentDelay = action.Nodes["delay"];
                    if (parentDelay != null)
                    {
                        int delay = ReadInteger(parentDelay, "delay");
                        if (delay == 0)
                        {
                            delay = 120;
                            writer.Warn(parentDelay.FullPathToFile, "0ms 환상 반지 동작 지연을 원본 렌더러 기본값 120ms로 정규화했습니다.");
                        }
                        foreach (WzSpriteFrame frame in track.frames)
                        {
                            Wz_Node source = UnityEntityExporter.ResolveUol(PluginManager.FindWz(frame.sourcePath), PluginManager.FindWz);
                            if (source == null) throw new InvalidDataException("환상 반지 원본 프레임을 확인할 수 없습니다.");
                            if (source.Nodes["delay"] == null) frame.delayMs = Math.Abs((double)delay);
                        }
                    }
                    for (int frame = 0; frame < track.frames.Count; frame++)
                        track.metadata.Add(new WzMetadata { key = "frame/" + frame + "/source", value = track.frames[frame].sourcePath });
                    entity.clips.Add(new WzAnimationClip
                    {
                        name = name, loop = loop, durationMs = track.frames.Sum(frame => frame.delayMs),
                        tracks = new List<WzAnimationTrack> { track }
                    });
                    reportProgress?.Invoke(index + 1, names.Length, name + " 완료");
                }
                if (entity.clips.Count == 0) throw new InvalidDataException("환상 반지에서 내보낼 수 있는 원본 동작을 찾지 못했습니다.");
                entity.defaultAction = entity.clips.Any(clip => clip.name == "stand1") ? "stand1" : entity.clips[0].name;
                writer.Manifest.entities.Add(entity);
                writer.Commit();
                return writer.Manifest;
            }
        }

        private static int ReadInteger(Wz_Node node, string property)
        {
            node = UnityEntityExporter.ResolveUol(node, PluginManager.FindWz);
            if (node?.Value == null || !int.TryParse(Convert.ToString(node.Value, CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new InvalidDataException("환상 반지 원본 " + property + " 값이 유효하지 않습니다.");
            return value;
        }
    }
}
