using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using WzComparerR2.Animation;
using WzComparerR2.Common;
using WzComparerR2.Controls;
using WzComparerR2.UnityExport;
using WzComparerR2.WzLib;
using WzUnity;

namespace WzComparerR2.MapRender.Export
{
    /// <summary>Optional GPU export hook; the main executable does not reference this plugin.</summary>
    public static class UnitySpineExportBridge
    {
        public static void Register() => UnityEntityExporter.SpecialAnimationExporter = Export;

        public static List<WzAnimationClip> Export(Wz_Node node, UnityExportWriter writer,
            GlobalFindNodeFunction findNode, GraphicsDevice graphicsDevice)
        {
            var clips = new List<WzAnimationClip>();
            if (graphicsDevice == null || graphicsDevice.IsDisposed)
            {
                writer.Warn(node.FullPathToFile, "Spine export requires an active graphics device.");
                return clips;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string scratch = Path.Combine(Path.GetTempPath(), "WzUnitySpine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                foreach (SpineDetectionResult detection in Detect(node, 0))
                {
                    writer.CancellationToken.ThrowIfCancellationRequested();
                    if (!seen.Add(detection.ResolvedSkelNode.FullPathToFile)) continue;
                    var loader = new WzSpineTextureLoader(detection.SourceNode.ParentNode, graphicsDevice, findNode);
                    ISpineAnimationData data = detection.Version == SpineVersion.V2
                        ? (ISpineAnimationData)SpineAnimationDataV2.Create(detection, loader)
                        : SpineAnimationDataV4.Create(detection, loader);
                    if (data == null) throw new InvalidDataException("Failed to load detected Spine data: " + detection.SourceNode.FullPathToFile);
                    try
                    {
                        ISpineAnimator animator = data.CreateAnimator();
                        var names = animator.Animations.Count > 0 ? animator.Animations.ToArray() : new[] { string.Empty };
                        foreach (string name in names)
                        {
                            writer.CancellationToken.ThrowIfCancellationRequested();
                            animator.SelectedAnimationName = name;
                            int duration = Math.Max(0, (animator as AnimationItem)?.Length ?? 0);
                            string folder = "sequence-" + clips.Count;
                            var result = new MapRenderSpineSequenceBaker(graphicsDevice).Bake(new MapSpineBakeRequest
                            {
                                PackageRootDirectory = scratch, RelativeOutputDirectory = folder, AssetId = folder,
                                FrameRate = 30, CycleDurationMs = duration,
                                DurationFrames = Math.Max(1, (int)Math.Ceiling(duration * 30d / 1000d)),
                                Animator = animator, AnimationName = name, SkinName = animator.SelectedSkin
                            }, writer.CancellationToken);
                            if (result.BlendMode != "normal")
                                writer.Warn(detection.SourceNode.FullPathToFile, "Spine blend mode '" + result.BlendMode + "' is retained as metadata; the Unity importer currently renders it with normal sprite blending.");
                            var clip = new WzAnimationClip { name = string.IsNullOrEmpty(name) ? "default" : name, loop = result.Loop };
                            var track = new WzAnimationTrack { id = "spine", kind = "spine-sequence", loop = result.Loop };
                            foreach (MapSpineBakeFrame frame in result.Frames)
                            {
                                writer.CancellationToken.ThrowIfCancellationRequested();
                                track.frames.Add(new WzSpriteFrame
                                {
                                    assetId = writer.AddPngFile(Path.Combine(scratch, frame.RelativePath.Replace('/', Path.DirectorySeparatorChar))),
                                    originX = frame.OriginX, originY = frame.OriginY, delayMs = Math.Max(1, frame.DelayMs),
                                    blend = result.BlendMode, sourcePath = detection.SourceNode.FullPathToFile
                                });
                            }
                            clip.durationMs = track.frames.Sum(frame => frame.delayMs);
                            clip.tracks.Add(track);
                            clips.Add(clip);
                            foreach (string warning in result.Warnings) writer.Warn(detection.SourceNode.FullPathToFile, warning);
                        }
                    }
                    finally { (data.Atlas as IDisposable)?.Dispose(); }
                }
            }
            finally
            {
                // Unique scratch directory owned solely by this call.
                if (Directory.Exists(scratch))
                {
                    try { Directory.Delete(scratch, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return clips;
        }

        private static IEnumerable<SpineDetectionResult> Detect(Wz_Node node, int depth)
        {
            if (node == null || depth > 4) yield break;
            node = node.ResolveUol();
            if (node == null) yield break;
            SpineDetectionResult detection = SpineLoader.Detect(node);
            if (detection.Success) { yield return detection; yield break; }
            if (node.Value != null) yield break;
            foreach (var child in node.Nodes)
            {
                // Action metadata may contain a separate effect, exported by its own track.
                if (child.Text == "info") continue;
                foreach (var result in Detect(child, depth + 1)) yield return result;
            }
        }
    }
}
