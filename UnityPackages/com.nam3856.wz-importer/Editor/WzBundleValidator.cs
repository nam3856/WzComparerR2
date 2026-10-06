using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using WzUnity;
using Newtonsoft.Json;

namespace WzComparerR2.Unity.Editor
{
    public static class WzBundleValidator
    {
        public static WzUnityManifest ReadAndValidate(string directory)
        {
            directory = Path.GetFullPath(directory);
            string manifestPath = Path.Combine(directory, "wz-unity.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("The selected folder does not contain wz-unity.json.");
            // JsonUtility creates empty objects for explicit JSON null, losing optional map/motion semantics.
            var manifest = JsonConvert.DeserializeObject<WzUnityManifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.schemaVersion != 1) throw new InvalidDataException("Unsupported WZ Unity schema. Expected version 1.");
            Require(!string.IsNullOrWhiteSpace(manifest.id), "Bundle id is missing.");
            Require(Finite(manifest.pixelsPerUnit) && manifest.pixelsPerUnit > 0, "pixelsPerUnit must be positive.");
            Require(manifest.assets != null && manifest.entities != null && manifest.entities.Count > 0, "Bundle must contain entities and an asset table.");
            var assets = new Dictionary<string, WzPngAsset>(StringComparer.Ordinal);
            foreach (var asset in manifest.assets)
            {
                Require(asset != null && !string.IsNullOrWhiteSpace(asset.id) && !assets.ContainsKey(asset.id), "Empty or duplicate PNG asset id.");
                assets.Add(asset.id, asset);
                Require(asset.width > 0 && asset.height > 0 && asset.width <= 16384 && asset.height <= 16384, "PNG dimensions exceed Unity's supported texture size: " + asset.id);
                string file = ResolveInside(directory, asset.file);
                Require(File.Exists(file), "Missing PNG: " + asset.file);
                byte[] bytes = File.ReadAllBytes(file);
                Require(bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 && bytes[6] == 26 && bytes[7] == 10, "Invalid PNG: " + asset.file);
                Require(ReadInt(bytes, 16) == asset.width && ReadInt(bytes, 20) == asset.height, "PNG dimensions disagree with manifest: " + asset.file);
                if (!string.IsNullOrEmpty(asset.sha256))
                {
                    using (var hash = SHA256.Create())
                        Require(string.Equals(Hex(hash.ComputeHash(bytes)), asset.sha256, StringComparison.OrdinalIgnoreCase), "PNG checksum mismatch: " + asset.file);
                }
            }
            var entities = new Dictionary<string, WzEntity>(StringComparer.Ordinal);
            foreach (var entity in manifest.entities)
            {
                Require(entity != null && !string.IsNullOrWhiteSpace(entity.id) && !entities.ContainsKey(entity.id), "Empty or duplicate entity id.");
                entities.Add(entity.id, entity);
                Require(entity.clips != null && entity.clips.Count > 0, "Entity has no animations: " + entity.id);
                var clipNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var clip in entity.clips)
                {
                    Require(clip != null && !string.IsNullOrEmpty(clip.name) && clipNames.Add(clip.name), "Empty or duplicate action: " + entity.id);
                    Require(Finite(clip.durationMs) && clip.durationMs >= 0 && clip.tracks != null && clip.tracks.Count > 0, "Invalid action duration or tracks: " + clip.name);
                    var tracks = new Dictionary<string, WzUnity.WzAnimationTrack>(StringComparer.Ordinal);
                    foreach (var track in clip.tracks)
                    {
                        Require(track != null && !string.IsNullOrEmpty(track.id) && !tracks.ContainsKey(track.id), "Empty or duplicate track id: " + clip.name);
                        tracks.Add(track.id, track);
                        Require(Finite(track.startMs) && track.startMs >= 0 && track.frames != null && track.frames.Count > 0, "Invalid track: " + track.id);
                        foreach (var frame in track.frames)
                        {
                            Require(frame != null && Finite(frame.delayMs) && frame.delayMs > 0, "Frame duration must be positive: " + track.id);
                            Require(Finite(frame.x) && Finite(frame.y) && Finite(frame.originX) && Finite(frame.originY) && Finite(frame.scaleX) && Finite(frame.scaleY) && Finite(frame.rotation), "Invalid frame transform: " + track.id);
                            Require(string.IsNullOrEmpty(frame.assetId) || assets.ContainsKey(frame.assetId), "Unknown frame asset: " + frame.assetId);
                            Require(!frame.visible || !string.IsNullOrEmpty(frame.assetId), "Visible frame has no asset: " + track.id);
                        }
                    }
                    foreach (var track in clip.tracks)
                    {
                        if (string.IsNullOrEmpty(track.poseTrack)) continue;
                        Require(tracks.TryGetValue(track.poseTrack, out var parent), "Unknown pose track: " + track.poseTrack);
                        var visited = new HashSet<string> { track.id };
                        var cursor = track;
                        while (!string.IsNullOrEmpty(cursor.poseTrack))
                        {
                            Require(visited.Add(cursor.poseTrack) && tracks.TryGetValue(cursor.poseTrack, out cursor), "Cyclic or missing pose track: " + track.id);
                        }
                        foreach (var pose in track.poses ?? new List<WzTrackPose>())
                        {
                            Require(pose.poseFrame >= 0 && pose.poseFrame < parent.frames.Count && pose.frameIndex >= -1 && pose.frameIndex < track.frames.Count, "Pose frame index out of range: " + track.id);
                            Require(Finite(pose.x) && Finite(pose.y) && Finite(pose.originX) && Finite(pose.originY), "Invalid pose position: " + track.id);
                            Require(!pose.overrideSprite || string.IsNullOrEmpty(pose.assetId) || assets.ContainsKey(pose.assetId), "Unknown pose PNG: " + pose.assetId);
                        }
                    }
                }
                Require(clipNames.Contains(entity.defaultAction), "Default action does not exist: " + entity.id);
            }
            if (manifest.map != null)
            {
                var map = manifest.map;
                Require(Finite(map.left) && Finite(map.top) && Finite(map.width) && Finite(map.height) && map.width >= 0 && map.height >= 0, "Invalid map bounds.");
                foreach (var layer in map.layers ?? new List<WzMapLayer>())
                {
                    if (!string.IsNullOrEmpty(layer.entityId)) ValidateEntityReference(entities, layer.entityId, layer.action);
                    Require(Finite(layer.x) && Finite(layer.y) && Finite(layer.alpha), "Invalid map layer transform: " + layer.id);
                    if (layer.motion != null)
                    {
                        ValidateMotion(layer.motion.x); ValidateMotion(layer.motion.y);
                        ValidateMotion(layer.motion.opacity); ValidateMotion(layer.motion.scaleX);
                    }
                    if (layer.background != null)
                    {
                        var bg = layer.background;
                        Require(Finite(bg.rx) && Finite(bg.ry) && Finite(bg.cx) && Finite(bg.cy) && Finite(bg.scrollDistanceX) && Finite(bg.scrollDistanceY), "Invalid background: " + layer.id);
                    }
                }
                foreach (var placement in map.placements ?? new List<WzPlacement>())
                {
                    if (!string.IsNullOrEmpty(placement.entityId)) ValidateEntityReference(entities, placement.entityId, placement.action);
                    Require(Finite(placement.x) && Finite(placement.y), "Invalid placement: " + placement.id);
                }
                foreach (var foothold in map.footholds ?? new List<WzFoothold>())
                    Require(Finite(foothold.x1) && Finite(foothold.y1) && Finite(foothold.x2) && Finite(foothold.y2), "Invalid foothold coordinates.");
                foreach (var ladder in map.ladders ?? new List<WzLadder>())
                    Require(Finite(ladder.x) && Finite(ladder.y1) && Finite(ladder.y2), "Invalid ladder coordinates.");
                foreach (var portal in map.portals ?? new List<WzPortal>())
                    Require(Finite(portal.x) && Finite(portal.y), "Invalid portal coordinates.");
            }
            return manifest;
        }

        private static void ValidateEntityReference(Dictionary<string, WzEntity> entities, string id, string action)
        {
            Require(!string.IsNullOrEmpty(id) && entities.TryGetValue(id, out _), "Unknown map entity: " + id);
            if (!string.IsNullOrEmpty(action)) Require(entities[id].clips.Any(clip => clip.name == action), "Unknown map action: " + id + "/" + action);
        }

        private static void ValidateMotion(WzMotionChannel channel)
        {
            if (channel == null) return;
            Require(channel.kind == "sine" || channel.kind == "cosine" || channel.kind == "keyframes" || channel.kind == "constant", "Unknown motion kind: " + channel.kind);
            Require(Finite(channel.cycleMs) && channel.cycleMs >= 0 && Finite(channel.phaseMs) && Finite(channel.offset) && Finite(channel.amplitude), "Invalid motion parameters.");
            double lastTime = -1;
            foreach (var key in channel.keys ?? new List<WzMotionKey>())
            {
                Require(Finite(key.timeMs) && key.timeMs >= lastTime && Finite(key.value), "Invalid motion key.");
                Require(key.interpolation == "linear" || key.interpolation == "hold", "Unknown motion interpolation.");
                lastTime = key.timeMs;
            }
        }

        public static string ResolveInside(string directory, string relative)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative) && !relative.Contains(":"), "Asset path must be relative.");
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            Require(path.StartsWith(root, StringComparison.OrdinalIgnoreCase), "Asset path escapes bundle: " + relative);
            for (string current = path; current.Length >= root.Length; current = Path.GetDirectoryName(current))
                if (File.Exists(current) || Directory.Exists(current))
                    Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Symbolic links are not allowed in bundle asset paths.");
            return path;
        }

        internal static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static int ReadInt(byte[] bytes, int index) => bytes[index] << 24 | bytes[index + 1] << 16 | bytes[index + 2] << 8 | bytes[index + 3];
        internal static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
