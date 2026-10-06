using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using WzUnity;

namespace WzComparerR2.UnityExport
{
    /// <summary>Owns one export folder. Writes to a sibling staging folder and replaces only a matching managed export.</summary>
    public sealed class UnityExportWriter : IDisposable
    {
        private readonly string destination;
        private readonly string parent;
        private readonly CancellationToken cancellationToken;
        private bool committed;
        private readonly Dictionary<string, WzPngAsset> assets = new Dictionary<string, WzPngAsset>(StringComparer.Ordinal);

        public UnityExportWriter(string destination, string id, string kind, string sourcePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Export ID is required.", nameof(id));
            this.destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            parent = Path.GetDirectoryName(this.destination);
            if (string.IsNullOrEmpty(parent) || this.destination == Path.GetPathRoot(this.destination).TrimEnd('\\', '/'))
                throw new ArgumentException("Choose an export subfolder, not a drive root.", nameof(destination));
            this.cancellationToken = cancellationToken;
            Manifest = new WzUnityManifest { id = id, kind = kind, sourcePath = sourcePath };
            StagingDirectory = Path.Combine(parent, "." + Path.GetFileName(this.destination) + ".wz-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(StagingDirectory, "png"));
        }

        public WzUnityManifest Manifest { get; }
        public string StagingDirectory { get; }
        public CancellationToken CancellationToken => cancellationToken;

        public string AddBitmap(Bitmap bitmap)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return AddPngBytes(stream.ToArray(), bitmap.Width, bitmap.Height);
            }
        }

        public string AddPngFile(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes, false))
            using (var bitmap = new Bitmap(stream))
            {
                if (bitmap.RawFormat.Guid != ImageFormat.Png.Guid) throw new InvalidDataException("Expected PNG: " + path);
                return AddPngBytes(bytes, bitmap.Width, bitmap.Height);
            }
        }

        private string AddPngBytes(byte[] bytes, int width, int height)
        {
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (assets.ContainsKey(hash)) return hash;
            var asset = new WzPngAsset { id = hash, file = "png/" + hash + ".png", width = width, height = height, sha256 = hash };
            File.WriteAllBytes(Path.Combine(StagingDirectory, "png", hash + ".png"), bytes);
            assets.Add(hash, asset);
            Manifest.assets.Add(asset);
            return hash;
        }

        public void Warn(string sourcePath, string reason)
        {
            if (!Manifest.warnings.Any(w => w.sourcePath == sourcePath && w.reason == reason))
                Manifest.warnings.Add(new WzExportWarning { sourcePath = sourcePath, reason = reason });
        }

        public string Commit()
        {
            if (committed) throw new InvalidOperationException("Export has already been committed.");
            cancellationToken.ThrowIfCancellationRequested();
            Validate(Manifest, StagingDirectory);
            File.WriteAllText(Path.Combine(StagingDirectory, "wz-unity.json"), JsonConvert.SerializeObject(Manifest, Formatting.Indented), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(StagingDirectory, "export-report.txt"),
                $"{Manifest.kind}: {Manifest.id}\r\nAssets: {Manifest.assets.Count}\r\nEntities: {Manifest.entities.Count}\r\nWarnings: {Manifest.warnings.Count}\r\n" +
                string.Join("\r\n", Manifest.warnings.Select(w => w.sourcePath + ": " + w.reason)), new UTF8Encoding(false));

            string backup = null;
            if (Directory.Exists(destination))
            {
                string marker = Path.Combine(destination, "wz-unity.json");
                if (!File.Exists(marker)) throw new IOException("기존 폴더가 Unity 추출 결과가 아닙니다. 비어 있는 새 하위 폴더를 선택하세요: " + destination);
                var prior = JsonConvert.DeserializeObject<WzUnityManifest>(File.ReadAllText(marker));
                if (prior == null || prior.id != Manifest.id || prior.kind != Manifest.kind)
                    throw new IOException("다른 추출 결과가 있는 폴더는 덮어쓸 수 없습니다: " + destination);
                var managed = new HashSet<string>(prior.assets.Select(a => a.file.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase)
                { "wz-unity.json", "export-report.txt" };
                PreserveUnmanagedFiles(destination, destination, managed);
                backup = Path.Combine(parent, "." + Path.GetFileName(destination) + ".wz-backup-" + Guid.NewGuid().ToString("N"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (backup != null) Directory.Move(destination, backup);
                Directory.Move(StagingDirectory, destination);
                committed = true;
            }
            catch
            {
                if (backup != null && Directory.Exists(backup) && !Directory.Exists(destination)) Directory.Move(backup, destination);
                throw;
            }
            if (backup != null)
            {
                // A locked old output must not turn a successful publish into a reported failure.
                try { DeleteOwnedSibling(backup, ".wz-backup-"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            return destination;
        }

        private void PreserveUnmanagedFiles(string root, string current, HashSet<string> managed)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("추출 폴더에 연결된 디렉터리가 있어 교체하지 않습니다: " + current);
            foreach (string file in Directory.EnumerateFiles(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = file.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (managed.Contains(relative)) continue;
                string target = Path.Combine(StagingDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) throw new IOException("새 추출 결과와 사용자 파일 이름이 겹칩니다: " + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
            foreach (string directory in Directory.EnumerateDirectories(current)) PreserveUnmanagedFiles(root, directory, managed);
        }

        public static void Validate(WzUnityManifest manifest, string folder)
        {
            if (manifest == null || manifest.schemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.id)) throw new InvalidDataException("Unsupported or missing Unity manifest.");
            if (float.IsNaN(manifest.pixelsPerUnit) || float.IsInfinity(manifest.pixelsPerUnit) || manifest.pixelsPerUnit <= 0) throw new InvalidDataException("Invalid pixelsPerUnit.");
            var assetIds = new HashSet<string>(StringComparer.Ordinal);
            string root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var asset in manifest.assets)
            {
                if (string.IsNullOrEmpty(asset.id) || !assetIds.Add(asset.id) || asset.width <= 0 || asset.height <= 0) throw new InvalidDataException("Invalid or duplicate PNG asset.");
                if (string.IsNullOrEmpty(asset.file) || Path.IsPathRooted(asset.file)) throw new InvalidDataException("PNG paths must be relative.");
                string full = Path.GetFullPath(Path.Combine(root, asset.file.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new InvalidDataException("Missing or unsafe PNG path: " + asset.file);
            }
            var entityIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entity in manifest.entities)
            {
                if (string.IsNullOrEmpty(entity.id) || !entityIds.Add(entity.id)) throw new InvalidDataException("Invalid or duplicate entity ID.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var clip in entity.clips)
                {
                    if (string.IsNullOrEmpty(clip.name) || !names.Add(clip.name)) throw new InvalidDataException("Invalid or duplicate action name.");
                    if (!Finite(clip.durationMs) || clip.durationMs <= 0) throw new InvalidDataException("Invalid action duration.");
                    var trackIds = new HashSet<string>(clip.tracks.Select(t => t.id), StringComparer.Ordinal);
                    if (trackIds.Count != clip.tracks.Count || trackIds.Contains(null)) throw new InvalidDataException("Invalid or duplicate track ID.");
                    foreach (var track in clip.tracks)
                    {
                        if (!Finite(track.startMs) || track.startMs < 0 || track.frames.Count == 0) throw new InvalidDataException("Invalid animation track.");
                        if (!string.IsNullOrEmpty(track.poseTrack) && (!trackIds.Contains(track.poseTrack) || track.poseTrack == track.id)) throw new InvalidDataException("Invalid pose track reference.");
                        foreach (var frame in track.frames)
                        {
                            if (frame.visible && (string.IsNullOrEmpty(frame.assetId) || !assetIds.Contains(frame.assetId))) throw new InvalidDataException("Missing frame PNG: " + frame.assetId);
                            if (double.IsNaN(frame.delayMs) || double.IsInfinity(frame.delayMs) || frame.delayMs <= 0) throw new InvalidDataException("Frame delay must be positive.");
                            if (new double[] { frame.x, frame.y, frame.originX, frame.originY, frame.scaleX, frame.scaleY, frame.rotation }.Any(v => !Finite(v))) throw new InvalidDataException("Invalid frame transform.");
                            if (frame.a0 < 0 || frame.a0 > 255 || frame.a1 < 0 || frame.a1 > 255) throw new InvalidDataException("Invalid frame opacity.");
                        }
                        foreach (var pose in track.poses)
                        {
                            if (string.IsNullOrEmpty(track.poseTrack) || pose.poseFrame < 0 || pose.poseFrame >= clip.tracks.First(t => t.id == track.poseTrack).frames.Count || pose.frameIndex < -1 || pose.frameIndex >= track.frames.Count)
                                throw new InvalidDataException("Invalid pose index.");
                            if (pose.overrideSprite && pose.visible && !assetIds.Contains(pose.assetId)) throw new InvalidDataException("Missing pose PNG.");
                            if (new double[] { pose.x, pose.y, pose.originX, pose.originY }.Any(v => !Finite(v))) throw new InvalidDataException("Invalid pose transform.");
                        }
                    }
                }
                if (entity.clips.Count > 0 && !names.Contains(entity.defaultAction)) throw new InvalidDataException("Missing default action: " + entity.id);
            }
            if (manifest.map != null)
            {
                if (new double[] { manifest.map.left, manifest.map.top, manifest.map.width, manifest.map.height }.Any(v => !Finite(v)) || manifest.map.width <= 0 || manifest.map.height <= 0) throw new InvalidDataException("Invalid map bounds.");
                foreach (var layer in manifest.map.layers)
                    if (!string.IsNullOrEmpty(layer.entityId) && !entityIds.Contains(layer.entityId)) throw new InvalidDataException("Missing map layer entity: " + layer.entityId);
                foreach (var spawn in manifest.map.placements)
                    if (!string.IsNullOrEmpty(spawn.entityId) && !entityIds.Contains(spawn.entityId)) throw new InvalidDataException("Missing placement entity: " + spawn.entityId);
            }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private void DeleteOwnedSibling(string directory, string marker)
        {
            string full = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("." + Path.GetFileName(destination) + marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove a folder outside this export transaction.");
            Directory.Delete(full, true);
        }

        public void Dispose()
        {
            if (!committed && Directory.Exists(StagingDirectory)) DeleteOwnedSibling(StagingDirectory, ".wz-stage-");
        }
    }
}
