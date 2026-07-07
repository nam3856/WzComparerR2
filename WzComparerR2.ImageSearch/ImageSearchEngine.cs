using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using WzComparerR2.Common;
using WzComparerR2.WzLib;

namespace WzComparerR2.ImageSearch
{
    internal static class ImageSearchEngine
    {
        public static List<SearchResult> Search(
            IEnumerable<Wz_Node> roots,
            ImageBuffer reference,
            double threshold,
            int sizeTolerance,
            bool ignoreSize,
            bool includeSpine,
            int spineSamples,
            SpineFrameRenderer spineRenderer,
            int maxResults,
            IProgress<SearchProgress> progress,
            CancellationToken cancellationToken)
        {
            List<SearchResult> results = new List<SearchResult>();
            SearchProgress state = new SearchProgress();
            HashSet<string> spineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int reportCountdown = 0;

            foreach (Wz_Node root in roots.Where(root => root != null))
            {
                foreach (Wz_Node node in EnumerateNodes(root, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (node.Value is Wz_Image)
                    {
                        state.ImagesScanned++;
                    }

                    if (node.Value is Wz_Png png)
                    {
                        if (ignoreSize || IsCandidateSize(reference, png.Width, png.Height, sizeTolerance))
                        {
                            state.CandidatesCompared++;
                            SearchResult result = CompareNode(node, png, reference, threshold, sizeTolerance, ignoreSize, cancellationToken);
                            AddResult(ref results, result, state, maxResults);
                        }
                    }

                    if (includeSpine
                        && spineRenderer != null
                        && TryDetectSpine(node, out SpineDetectionResult detectionResult)
                        && spineKeys.Add(GetSpineKey(detectionResult)))
                    {
                        state.SpinesScanned++;
                        foreach (SearchResult result in CompareSpineNode(detectionResult, reference, threshold, sizeTolerance, ignoreSize, spineSamples, spineRenderer, state, cancellationToken))
                        {
                            AddResult(ref results, result, state, maxResults);
                        }
                    }

                    if (--reportCountdown <= 0)
                    {
                        state.CurrentPath = GetDisplayPath(node);
                        progress?.Report(CloneProgress(state));
                        reportCountdown = 25;
                    }
                }

                progress?.Report(CloneProgress(state));
            }

            return OrderResults(results)
                .Take(maxResults)
                .ToList();
        }

        private static IEnumerable<Wz_Node> EnumerateNodes(Wz_Node root, CancellationToken cancellationToken)
        {
            Stack<Wz_Node> stack = new Stack<Wz_Node>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Wz_Node node = stack.Pop();
                yield return node;

                if (node.Value is Wz_Image image)
                {
                    if (!image.TryExtract())
                    {
                        continue;
                    }

                    if (image.Node != null)
                    {
                        stack.Push(image.Node);
                    }
                }
                else
                {
                    PushChildren(stack, node);
                }
            }
        }

        private static void PushChildren(Stack<Wz_Node> stack, Wz_Node parent)
        {
            if (parent == null)
            {
                return;
            }

            for (int i = parent.Nodes.Count - 1; i >= 0; i--)
            {
                stack.Push(parent.Nodes[i]);
            }
        }

        private static bool IsCandidateSize(ImageBuffer reference, Wz_Png png, int sizeTolerance)
        {
            return IsCandidateSize(reference, png.Width, png.Height, sizeTolerance);
        }

        private static bool IsCandidateSize(ImageBuffer reference, int width, int height, int sizeTolerance)
        {
            return Math.Abs(reference.Width - width) <= sizeTolerance
                && Math.Abs(reference.Height - height) <= sizeTolerance;
        }

        private static SearchResult CompareNode(
            Wz_Node node,
            Wz_Png png,
            ImageBuffer reference,
            double threshold,
            int sizeTolerance,
            bool ignoreSize,
            CancellationToken cancellationToken)
        {
            try
            {
                using (Bitmap bitmap = png.ExtractPng())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    return CompareBitmap(node, bitmap, png.Width, png.Height, reference, threshold, sizeTolerance, ignoreSize, "PNG", null);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<SearchResult> CompareSpineNode(
            SpineDetectionResult detectionResult,
            ImageBuffer reference,
            double threshold,
            int sizeTolerance,
            bool ignoreSize,
            int spineSamples,
            SpineFrameRenderer spineRenderer,
            SearchProgress state,
            CancellationToken cancellationToken)
        {
            foreach (RenderedSpineFrame frame in spineRenderer.RenderFrames(detectionResult.SourceNode, spineSamples, cancellationToken))
            {
                using (frame)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    state.CandidatesCompared++;
                    string animationName = string.IsNullOrEmpty(frame.AnimationName) ? "setup" : frame.AnimationName;
                    SearchResult result = CompareBitmap(
                        detectionResult.SourceNode,
                        frame.Bitmap,
                        frame.Bitmap.Width,
                        frame.Bitmap.Height,
                        reference,
                        threshold,
                        sizeTolerance,
                        ignoreSize,
                        "Spine",
                        animationName + " @ " + frame.Time + "ms");

                    if (result != null)
                    {
                        yield return result;
                    }
                }
            }
        }

        private static SearchResult CompareBitmap(
            Wz_Node node,
            Bitmap bitmap,
            int width,
            int height,
            ImageBuffer reference,
            double threshold,
            int sizeTolerance,
            bool ignoreSize,
            string kind,
            string detail)
        {
            if (!ignoreSize && !IsCandidateSize(reference, width, height, sizeTolerance))
            {
                return null;
            }

            ImageBuffer candidate = ignoreSize && (bitmap.Width != reference.Width || bitmap.Height != reference.Height)
                ? ImageBuffer.FromBitmap(bitmap, reference.Width, reference.Height)
                : ImageBuffer.FromBitmap(bitmap);
            double score = CalculateScore(reference, candidate);
            if (score < threshold)
            {
                return null;
            }

            Wz_Image ownerImage = node.GetNodeWzImage();
            Wz_Node ownerNode = ownerImage?.OwnerNode;
            string relativePath = ownerImage == null ? null : GetRelativePath(ownerImage.Node, node);

            return new SearchResult
            {
                Node = node,
                ImageOwnerNode = ownerNode,
                RelativePath = relativePath,
                Kind = kind,
                Detail = detail,
                FullPath = GetDisplayPath(node),
                Width = width,
                Height = height,
                SizeDistance = GetSizeDistance(reference, width, height),
                Score = score
            };
        }

        private static IOrderedEnumerable<SearchResult> OrderResults(IEnumerable<SearchResult> results)
        {
            return results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.SizeDistance)
                .ThenBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase);
        }

        private static int GetSizeDistance(ImageBuffer reference, Wz_Png png)
        {
            return GetSizeDistance(reference, png.Width, png.Height);
        }

        private static int GetSizeDistance(ImageBuffer reference, int width, int height)
        {
            return Math.Abs(reference.Width - width) + Math.Abs(reference.Height - height);
        }

        private static void AddResult(ref List<SearchResult> results, SearchResult result, SearchProgress state, int maxResults)
        {
            if (result == null)
            {
                return;
            }

            results.Add(result);
            state.ResultsFound = results.Count;
            if (results.Count > maxResults * 2)
            {
                results = OrderResults(results)
                    .Take(maxResults)
                    .ToList();
                state.ResultsFound = results.Count;
            }
        }

        private static bool TryDetectSpine(Wz_Node node, out SpineDetectionResult detectionResult)
        {
            detectionResult = null;
            if (!MightBeSpineNode(node))
            {
                return false;
            }

            try
            {
                detectionResult = SpineLoader.Detect(node);
                return detectionResult.Success;
            }
            catch
            {
                detectionResult = null;
                return false;
            }
        }

        private static bool MightBeSpineNode(Wz_Node node)
        {
            if (node?.ParentNode == null)
            {
                return false;
            }

            string name = node.Text ?? string.Empty;
            if (name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".skel", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (node.Value is Wz_RawData)
            {
                return HasAtlasSibling(node);
            }

            if (node.Value is Wz_Sound sound && sound.SoundType == Wz_SoundType.Binary)
            {
                return HasAtlasSibling(node);
            }

            return false;
        }

        private static bool HasAtlasSibling(Wz_Node node)
        {
            if (node.ParentNode.Nodes[node.Text + ".atlas"] != null
                || node.ParentNode.Nodes["atlas"] != null)
            {
                return true;
            }

            foreach (Wz_Node sibling in node.ParentNode.Nodes)
            {
                if (sibling.Text.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetSpineKey(SpineDetectionResult detectionResult)
        {
            return GetDisplayPath(detectionResult.ResolvedAtlasNode) + "|" + GetDisplayPath(detectionResult.ResolvedSkelNode);
        }

        private static double CalculateScore(ImageBuffer reference, ImageBuffer candidate)
        {
            int width = Math.Min(reference.Width, candidate.Width);
            int height = Math.Min(reference.Height, candidate.Height);
            int refOffsetX = Math.Max(0, (reference.Width - width) / 2);
            int refOffsetY = Math.Max(0, (reference.Height - height) / 2);
            int candOffsetX = Math.Max(0, (candidate.Width - width) / 2);
            int candOffsetY = Math.Max(0, (candidate.Height - height) / 2);

            double weightedError = 0d;
            double totalWeight = 0d;

            for (int y = 0; y < height; y++)
            {
                int refRow = ((y + refOffsetY) * reference.Width + refOffsetX) * 4;
                int candRow = ((y + candOffsetY) * candidate.Width + candOffsetX) * 4;

                for (int x = 0; x < width; x++)
                {
                    int refIndex = refRow + x * 4;
                    int candIndex = candRow + x * 4;
                    int refAlpha = reference.Bgra[refIndex + 3];
                    double weight = 0.05d + 0.95d * refAlpha / 255d;

                    int db = Math.Abs(reference.Bgra[refIndex] - candidate.Bgra[candIndex]);
                    int dg = Math.Abs(reference.Bgra[refIndex + 1] - candidate.Bgra[candIndex + 1]);
                    int dr = Math.Abs(reference.Bgra[refIndex + 2] - candidate.Bgra[candIndex + 2]);
                    int da = Math.Abs(reference.Bgra[refIndex + 3] - candidate.Bgra[candIndex + 3]);
                    double pixelError = (db + dg + dr + da) / 1020d;

                    weightedError += pixelError * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight <= 0d)
            {
                return 0d;
            }

            double sizePenalty = 0d;
            if (reference.Width != candidate.Width || reference.Height != candidate.Height)
            {
                int refArea = reference.Width * reference.Height;
                int candArea = candidate.Width * candidate.Height;
                int extraArea = Math.Abs(refArea - candArea);
                sizePenalty = Math.Min(10d, 25d * extraArea / Math.Max(refArea, candArea));
            }

            return Math.Max(0d, 100d - weightedError / totalWeight * 100d - sizePenalty);
        }

        private static string GetRelativePath(Wz_Node root, Wz_Node node)
        {
            if (root == null || node == null || root == node)
            {
                return string.Empty;
            }

            Stack<string> parts = new Stack<string>();
            Wz_Node current = node;
            while (current != null && current != root)
            {
                parts.Push(current.Text);
                current = current.ParentNode;
            }

            return string.Join("\\", parts.ToArray());
        }

        private static string GetDisplayPath(Wz_Node node)
        {
            if (node == null)
            {
                return string.Empty;
            }

            Wz_Image ownerImage = node.GetNodeWzImage();
            if (ownerImage?.OwnerNode != null)
            {
                string relativePath = GetRelativePath(ownerImage.Node, node);
                return string.IsNullOrEmpty(relativePath)
                    ? ownerImage.OwnerNode.FullPathToFile
                    : ownerImage.OwnerNode.FullPathToFile + "\\" + relativePath;
            }

            return node.FullPathToFile;
        }

        private static SearchProgress CloneProgress(SearchProgress progress)
        {
            return new SearchProgress
            {
                ImagesScanned = progress.ImagesScanned,
                SpinesScanned = progress.SpinesScanned,
                CandidatesCompared = progress.CandidatesCompared,
                ResultsFound = progress.ResultsFound,
                CurrentPath = progress.CurrentPath
            };
        }
    }
}
