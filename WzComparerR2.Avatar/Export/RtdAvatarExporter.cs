using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using WzComparerR2.AvatarCommon;
using AvatarAction = WzComparerR2.AvatarCommon.Action;

namespace WzComparerR2.Avatar.Export
{
    internal sealed class RtdAvatarExporter
    {
        public const int FrameRate = 30;
        public const int MaxMasterFrames = 3 * 60 * 60 * FrameRate;
        public const int MaxCanvasDimension = 30000;
        private readonly AvatarCanvas avatar;
        private int nextAssetId;

        public RtdAvatarExporter(AvatarCanvas avatar)
        {
            this.avatar = avatar ?? throw new ArgumentNullException(nameof(avatar));
        }

        public RtdExportEstimate Estimate(IEnumerable<AvatarAction> selectedActions)
        {
            var actions = selectedActions?.ToArray() ?? new AvatarAction[0];
            var tokenAllocator = new FileTokenAllocator();
            var effectDefinitions = new Dictionary<string, RtdEffect>(StringComparer.Ordinal);
            int validActions = 0;
            int skippedActions = 0;
            int effectTracks = 0;
            int blinkOmittedActions = 0;
            int fallbackActions = 0;
            int maxMasterFrames = 0;
            long totalMasterFrames = 0;
            long intervals = 0;
            for (int index = 0; index < actions.Length; index++)
            {
                AvatarAction action = actions[index];
                string fileToken = tokenAllocator.Allocate(action.Name);
                string actionId = "action-" + (index + 1).ToString("D4", CultureInfo.InvariantCulture) + "-" + fileToken;
                ActionWork work = BuildActionWork(
                    actionId,
                    action.Name,
                    fileToken,
                    effectDefinitions,
                    CancellationToken.None,
                    false);
                if (work == null)
                {
                    skippedActions++;
                    continue;
                }
                try
                {
                    validActions++;
                    effectTracks += work.Tracks.Count;
                    if (work.BlinkOmitted) blinkOmittedActions++;
                    if (string.Equals(work.Manifest.MasterMode, "longestFallback", StringComparison.Ordinal)) fallbackActions++;
                    maxMasterFrames = Math.Max(maxMasterFrames, work.Manifest.MasterFrames);
                    totalMasterFrames = checked(totalMasterFrames + work.Manifest.MasterFrames);
                    intervals = checked(intervals + Math.Max(0, CreateIntervalBoundaries(work).Count - 1));
                }
                finally
                {
                    work.DisposeOwnedBitmaps();
                }
            }
            return new RtdExportEstimate(
                validActions,
                skippedActions,
                effectTracks,
                intervals,
                maxMasterFrames,
                totalMasterFrames,
                fallbackActions,
                blinkOmittedActions);
        }

        public RtdExportResult Export(
            string outputDirectory,
            IEnumerable<AvatarAction> selectedActions,
            bool claimLegacyFiles,
            CancellationToken cancellationToken,
            Action<int, int, string> reportProgress)
        {
            if (avatar.Body == null || avatar.Head == null)
            {
                throw new InvalidOperationException("캐릭터가 없습니다.");
            }

            string[] actionNames = selectedActions.Select(action => action.Name).Distinct(StringComparer.Ordinal).ToArray();
            if (actionNames.Length == 0)
            {
                throw new InvalidOperationException("내보낼 동작이 없습니다.");
            }

            string outputFull = Path.GetFullPath(outputDirectory);
            string parent = Directory.GetParent(outputFull)?.FullName ?? outputFull;
            string directoryName = new DirectoryInfo(outputFull).Name;
            if (string.IsNullOrEmpty(directoryName)) directoryName = "avatar";
            string stage = Path.Combine(parent, "." + directoryName + ".rtd-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);

            var manifest = new RtdAvatarManifest();
            var tokenAllocator = new FileTokenAllocator();
            var effectDefinitions = new Dictionary<string, RtdEffect>(StringComparer.Ordinal);
            var result = new RtdExportResult();
            nextAssetId = 1;

            Exception exportFailure = null;
            try
            {
                for (int index = 0; index < actionNames.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string actionName = actionNames[index];
                    reportProgress?.Invoke(index, actionNames.Length, actionName + " 기본/표정 상태 분석 중...");

                    string fileToken = tokenAllocator.Allocate(actionName);
                    string actionId = "action-" + (index + 1).ToString("D4", CultureInfo.InvariantCulture) + "-" + fileToken;
                    var work = BuildActionWork(
                        actionId,
                        actionName,
                        fileToken,
                        effectDefinitions,
                        cancellationToken,
                        true,
                        phase => reportProgress?.Invoke(index, actionNames.Length, actionName + " " + phase));
                    if (work == null)
                    {
                        result.SkippedActions++;
                        reportProgress?.Invoke(index + 1, actionNames.Length, actionName + " 건너뜀");
                        continue;
                    }
                    try
                    {
                        RenderAction(
                            stage,
                            manifest,
                            work,
                            cancellationToken,
                            phase => reportProgress?.Invoke(index, actionNames.Length, actionName + " " + phase));
                        manifest.Actions.Add(work.Manifest);
                        result.ActionCount++;
                        result.EffectTrackCount += work.Tracks.Count;
                        result.WarningCount += work.Manifest.Warnings.Count;
                        if (work.BlinkOmitted) result.BlinkOmittedActionCount++;
                        if (string.Equals(work.Manifest.MasterMode, "longestFallback", StringComparison.Ordinal))
                        {
                            result.LongestFallbackActionCount++;
                        }
                        reportProgress?.Invoke(index + 1, actionNames.Length, actionName + " 완료");
                    }
                    finally
                    {
                        work.DisposeOwnedBitmaps();
                    }
                }

                foreach (RtdEffect effect in effectDefinitions.Values
                    .OrderBy(item => item.SlotIndex)
                    .ThenBy(item => item.ItemId)
                    .ThenBy(item => item.Id, StringComparer.Ordinal))
                {
                    manifest.Effects.Add(effect);
                }

                manifest.ManagedFiles.Sort(StringComparer.Ordinal);
                manifest.Assets.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(actionNames.Length, actionNames.Length,
                    "staging PNG/hash/size 검증 중... (asset "
                    + manifest.Assets.Count.ToString("N0", CultureInfo.CurrentCulture) + "개)");
                ValidateStage(stage, manifest);
                reportProgress?.Invoke(actionNames.Length, actionNames.Length, "rtd-avatar.json 작성 중...");
                RtdAvatarJsonWriter.Write(Path.Combine(stage, "rtd-avatar.json"), manifest);

                cancellationToken.ThrowIfCancellationRequested();
                reportProgress?.Invoke(actionNames.Length, actionNames.Length, "RTD 세트 반영 중...");
                RtdAvatarSetCommitter.Commit(stage, outputFull, claimLegacyFiles);
                result.AssetCount = manifest.Assets.Count;
                result.OutputDirectory = outputFull;
                return result;
            }
            catch (Exception ex)
            {
                exportFailure = ex;
                throw;
            }
            finally
            {
                if (Directory.Exists(stage))
                {
                    try
                    {
                        Directory.Delete(stage, true);
                    }
                    catch
                    {
                        if (exportFailure == null)
                        {
                            throw;
                        }
                    }
                }
            }
        }

        private ActionWork BuildActionWork(
            string actionId,
            string actionName,
            string fileToken,
            IDictionary<string, RtdEffect> effectDefinitions,
            CancellationToken cancellationToken,
            bool buildIntervals,
            Action<string> reportPhase = null)
        {
            ActionFrame[] bodyFrames = avatar.GetActionFrames(actionName);
            if (bodyFrames.Length == 0)
            {
                return null;
            }

            ActionFrame defaultFace = avatar.GetFaceFrames("default").FirstOrDefault() ?? new ActionFrame("default", 0);
            ActionFrame[] blinkFrames = avatar.GetFaceFrames("blink");
            ActionFrame blink1 = blinkFrames.Length > 1 ? blinkFrames[1] : null;
            ActionFrame blink2 = blinkFrames.Length > 2 ? blinkFrames[2] : null;
            var work = new ActionWork
            {
                Manifest = new RtdAction
                {
                    Id = actionId,
                    Name = actionName,
                    FileToken = fileToken
                },
                BodyFrames = bodyFrames,
                DefaultFace = defaultFace,
                Blink1 = blink1,
                Blink2 = blink2
            };
            try
            {
            reportPhase?.Invoke("기본/표정 primitive 분석 중...");
            var emptyEffects = new ActionFrame[AvatarCanvas.LayerSlotLength];
            for (int bodyIndex = 0; bodyIndex < bodyFrames.Length; bodyIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var variants = new BaseVariants
                {
                    Default = RenderState(bodyFrames[bodyIndex], defaultFace, emptyEffects)
                };
                if (blink1 != null && blink2 != null)
                {
                    variants.Blink1 = RenderState(bodyFrames[bodyIndex], blink1, emptyEffects);
                    variants.Blink2 = RenderState(bodyFrames[bodyIndex], blink2, emptyEffects);
                    string defaultKey = GetVisualKey(variants.Default);
                    variants.HasBlink = !string.Equals(defaultKey, GetVisualKey(variants.Blink1), StringComparison.Ordinal)
                        || !string.Equals(defaultKey, GetVisualKey(variants.Blink2), StringComparison.Ordinal);
                }
                work.BaseVariants.Add(variants);
            }

            int[] desiredBodyIndices = IsFourStepAction(actionName) && bodyFrames.Length >= 3
                ? new[] { 0, 1, 2, 1 }
                : Enumerable.Range(0, bodyFrames.Length).ToArray();
            foreach (int bodyIndex in desiredBodyIndices)
            {
                work.BaseTimeline.Add(new BaseTimelineFrame(bodyIndex, "default", 0, 14));
            }

            var candidates = work.BaseTimeline.Select((frame, index) => new { frame, index })
                .Where(item => work.BaseVariants[item.frame.BodyFrame].HasBlink)
                .Select(item => item.index)
                .ToArray();
            work.BlinkOmitted = candidates.Length == 0;
            if (work.BlinkOmitted)
            {
                work.Manifest.Warnings.Add("표정이 없거나 기본 표정과 같아 blink를 생략했습니다.");
            }
            if (candidates.Length > 0)
            {
                int selected = candidates[(int)(Fnv1a("avatar" + actionName) % (uint)candidates.Length)];
                int bodyIndex = work.BaseTimeline[selected].BodyFrame;
                work.BaseTimeline.Insert(selected + 1, new BaseTimelineFrame(bodyIndex, "blink", 1, 2));
                work.BaseTimeline.Insert(selected + 2, new BaseTimelineFrame(bodyIndex, "blink", 2, 3));
            }

            reportPhase?.Invoke("이펙트 track 분석 중...");
            for (int slot = 0; slot < AvatarCanvas.LayerSlotLength; slot++)
            {
                if (!TryGetEffectDefinition(slot, out EffectDefinition definition))
                {
                    continue;
                }
                ActionFrame[] frames = avatar.GetEffectFrames(actionName, slot);
                if (frames.Length == 0)
                {
                    continue;
                }

                if (!effectDefinitions.ContainsKey(definition.Id))
                {
                    effectDefinitions.Add(definition.Id, definition.Manifest);
                }
                work.Tracks.Add(BuildTrack(actionName, slot, definition, frames));
            }

            int bodyCycle = work.BaseTimeline.Sum(frame => frame.DurationFrames);
            var cycles = new List<int> { bodyCycle };
            cycles.AddRange(work.Tracks.Select(track => track.Manifest.CycleFrames));
            if (cycles.Any(cycle => cycle > MaxMasterFrames))
            {
                throw new InvalidOperationException(actionName + ": 단일 애니메이션 cycle이 After Effects 3시간 한도를 넘습니다.");
            }

            long lcm = cycles[0];
            bool fallback = false;
            for (int i = 1; i < cycles.Count; i++)
            {
                long gcd = GreatestCommonDivisor(lcm, cycles[i]);
                long next = lcm / gcd * cycles[i];
                if (next > MaxMasterFrames)
                {
                    fallback = true;
                    break;
                }
                lcm = next;
            }
            work.Manifest.MasterMode = fallback ? "longestFallback" : "commonLcm";
            work.Manifest.MasterFrames = fallback ? cycles.Max() : (int)lcm;
            if (fallback)
            {
                work.Manifest.Warnings.Add("공통 LCM이 3시간을 넘어 가장 긴 단일 cycle 길이로 내보냈습니다.");
            }

            if (buildIntervals)
            {
                BuildIntervals(work, cancellationToken, reportPhase);
            }
            return work;
            }
            catch
            {
                work.DisposeOwnedBitmaps();
                throw;
            }
        }

        private EffectTrackWork BuildTrack(string actionName, int slot, EffectDefinition definition, ActionFrame[] frames)
        {
            var manifest = new RtdEffectTrack
            {
                EffectId = definition.Id,
                Branch = GetEffectBranch(slot),
                ResolvedAction = GetResolvedEffectAction(actionName, slot)
            };
            long cumulativeMs = 0;
            long cumulativeFrames = 0;
            for (int index = 0; index < frames.Length; index++)
            {
                long delay = Math.Abs((long)frames[index].Delay);
                if (delay == 0) delay = 120;
                try
                {
                    cumulativeMs = checked(cumulativeMs + delay);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidOperationException(actionName + ": 이펙트 delay 누적값이 허용 범위를 넘습니다.", ex);
                }

                long maxMasterMilliseconds = MaxMasterFrames * 1000L / FrameRate;
                if (cumulativeMs > maxMasterMilliseconds)
                {
                    throw new InvalidOperationException(actionName + ": 이펙트 slot " + slot
                        + "의 단일 cycle이 After Effects 3시간 한도를 넘습니다.");
                }

                long roundedBoundary = (cumulativeMs * FrameRate + 500L) / 1000L;
                if (roundedBoundary <= cumulativeFrames) roundedBoundary = cumulativeFrames + 1;
                if (roundedBoundary > MaxMasterFrames)
                {
                    throw new InvalidOperationException(actionName + ": 이펙트 slot " + slot
                        + "의 양자화된 단일 cycle이 After Effects 3시간 한도를 넘습니다.");
                }
                int duration = checked((int)(roundedBoundary - cumulativeFrames));
                cumulativeFrames = roundedBoundary;
                manifest.Frames.Add(new RtdEffectFrame
                {
                    SourceFrameIndex = index,
                    DelayMs = checked((int)delay),
                    DurationFrames = duration,
                    A0 = ClampOpacity(frames[index].A0),
                    A1 = ClampOpacity(frames[index].A1)
                });
            }
            manifest.CycleFrames = checked((int)cumulativeFrames);
            return new EffectTrackWork(slot, definition, frames, manifest);
        }

        private void BuildIntervals(ActionWork work, CancellationToken cancellationToken, Action<string> reportPhase)
        {
            int masterFrames = work.Manifest.MasterFrames;
            int bodyCycle = work.BaseTimeline.Sum(frame => frame.DurationFrames);
            reportPhase?.Invoke("interval boundary 계산 중... (master "
                + masterFrames.ToString("N0", CultureInfo.CurrentCulture) + " frames, track "
                + work.Tracks.Count.ToString("N0", CultureInfo.CurrentCulture) + "개)");
            int[] points = CreateIntervalBoundaries(work).ToArray();
            int intervalCount = Math.Max(0, points.Length - 1);
            reportPhase?.Invoke("interval 0/" + intervalCount.ToString("N0", CultureInfo.CurrentCulture)
                + ": bounds 분석 중...");
            for (int i = 0; i < points.Length - 1; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = points[i];
                int duration = points[i + 1] - start;
                BaseTimelineFrame baseState = SelectTimelineFrame(work.BaseTimeline, start % bodyCycle, out _);
                var effectStates = new List<SelectedEffectState>();
                var effectActions = new ActionFrame[AvatarCanvas.LayerSlotLength];
                foreach (EffectTrackWork track in work.Tracks)
                {
                    RtdEffectFrame manifestFrame = SelectTimelineFrame(track.Manifest.Frames, start % track.Manifest.CycleFrames, out int localFrame);
                    ActionFrame actionFrame = track.SourceFrames[manifestFrame.SourceFrameIndex];
                    effectActions[track.Slot] = actionFrame;
                    effectStates.Add(new SelectedEffectState(track, manifestFrame, localFrame));
                }

                ActionFrame face = baseState.ExpressionIndex == 1 ? work.Blink1
                    : baseState.ExpressionIndex == 2 ? work.Blink2
                    : work.DefaultFace;
                AvatarRenderPrimitive[] primitives = RenderState(work.BodyFrames[baseState.BodyFrame], face, effectActions);
                try
                {
                    foreach (AvatarRenderPrimitive primitive in primitives)
                    {
                        Rectangle primitiveBounds = new Rectangle(primitive.Position, primitive.Bitmap.Size);
                        work.Bounds = work.Bounds.IsEmpty ? primitiveBounds : Rectangle.Union(work.Bounds, primitiveBounds);
                    }
                }
                finally
                {
                    DisposeOwnedPrimitives(primitives);
                }
                work.IntervalWorks.Add(new IntervalWork(start, duration, baseState, effectStates));
                if (ShouldReportIntervalProgress(i, intervalCount))
                {
                    reportPhase?.Invoke("interval "
                        + (i + 1).ToString("N0", CultureInfo.CurrentCulture) + "/"
                        + intervalCount.ToString("N0", CultureInfo.CurrentCulture) + ": bounds 분석 중...");
                }
            }

            foreach (BaseVariants variants in work.BaseVariants)
            {
                IncludeBounds(work, variants.Default);
                if (variants.HasBlink)
                {
                    IncludeBounds(work, variants.Blink1);
                    IncludeBounds(work, variants.Blink2);
                }
            }
            if (work.Bounds.IsEmpty) work.Bounds = new Rectangle(0, 0, 1, 1);
            if (work.Bounds.Width > MaxCanvasDimension || work.Bounds.Height > MaxCanvasDimension)
            {
                throw new InvalidOperationException(work.Manifest.Name + ": 공통 캔버스가 30,000px 한도를 넘습니다 ("
                    + work.Bounds.Width + "x" + work.Bounds.Height + ").");
            }
            work.Manifest.CanvasWidth = work.Bounds.Width;
            work.Manifest.CanvasHeight = work.Bounds.Height;
        }

        private static SortedSet<int> CreateIntervalBoundaries(ActionWork work)
        {
            int masterFrames = work.Manifest.MasterFrames;
            int bodyCycle = work.BaseTimeline.Sum(frame => frame.DurationFrames);
            var boundaries = new SortedSet<int> { 0, masterFrames };
            AddRepeatedBoundaries(boundaries, masterFrames, work.BaseTimeline.Select(frame => frame.DurationFrames), bodyCycle);
            foreach (EffectTrackWork track in work.Tracks)
            {
                AddRepeatedBoundaries(
                    boundaries,
                    masterFrames,
                    track.Manifest.Frames.Select(frame => frame.DurationFrames),
                    track.Manifest.CycleFrames);
            }
            return boundaries;
        }

        private void RenderAction(
            string stage,
            RtdAvatarManifest manifest,
            ActionWork work,
            CancellationToken cancellationToken,
            Action<string> reportPhase)
        {
            reportPhase?.Invoke("기본 PNG 저장 중...");
            var basePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int bodyIndex = 0; bodyIndex < work.BaseVariants.Count; bodyIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BaseVariants variants = work.BaseVariants[bodyIndex];
                string defaultPath = "avatar_" + work.Manifest.FileToken + "(" + bodyIndex + ")_default(0).png";
                SaveAsset(stage, manifest, defaultPath, RenderPlane(work.Bounds, variants.Default), null);
                basePaths.Add(BasePathKey(bodyIndex, 0), defaultPath);

                if (variants.HasBlink)
                {
                    string blink1Path = "avatar_" + work.Manifest.FileToken + "(" + bodyIndex + ")_blink(1).png";
                    string blink2Path = "avatar_" + work.Manifest.FileToken + "(" + bodyIndex + ")_blink(2).png";
                    SaveAsset(stage, manifest, blink1Path, RenderPlane(work.Bounds, variants.Blink1), null);
                    SaveAsset(stage, manifest, blink2Path, RenderPlane(work.Bounds, variants.Blink2), null);
                    basePaths.Add(BasePathKey(bodyIndex, 1), blink1Path);
                    basePaths.Add(BasePathKey(bodyIndex, 2), blink2Path);
                }
            }

            foreach (BaseTimelineFrame frame in work.BaseTimeline)
            {
                work.Manifest.BaseFrames.Add(new RtdBaseFrame
                {
                    BodyFrame = frame.BodyFrame,
                    Expression = frame.Expression,
                    ExpressionIndex = frame.ExpressionIndex,
                    Path = basePaths[BasePathKey(frame.BodyFrame, frame.ExpressionIndex)],
                    DurationFrames = frame.DurationFrames
                });
            }
            foreach (EffectTrackWork track in work.Tracks) work.Manifest.EffectTracks.Add(track.Manifest);

            var planeAssetCache = new Dictionary<string, string>(StringComparer.Ordinal);
            var planeAssetShaById = new Dictionary<string, string>(StringComparer.Ordinal);
            int planeRoundingIntervals = 0;
            long planeRoundingPixels = 0;
            int maxPlaneRoundingDelta = 0;
            int maxPlaneRoundingFrame = -1;
            int maxPlaneRoundingX = -1;
            int maxPlaneRoundingY = -1;
            uint maxPlaneExpectedArgb = 0;
            uint maxPlaneExpectedPremultipliedArgb = 0;
            uint maxPlaneActualArgb = 0;
            uint maxPlaneActualPremultipliedArgb = 0;
            int intervalCount = work.IntervalWorks.Count;
            reportPhase?.Invoke("PNG 0/" + intervalCount.ToString("N0", CultureInfo.CurrentCulture)
                + ": depth/effect 렌더링 및 검증 중...");
            for (int intervalIndex = 0; intervalIndex < intervalCount; intervalIndex++)
            {
                IntervalWork intervalWork = work.IntervalWorks[intervalIndex];
                cancellationToken.ThrowIfCancellationRequested();
                var interval = new RtdInterval { StartFrame = intervalWork.Start, DurationFrames = intervalWork.Duration };
                var effectActions = new ActionFrame[AvatarCanvas.LayerSlotLength];
                foreach (SelectedEffectState state in intervalWork.EffectStates)
                {
                    effectActions[state.Track.Slot] = state.Track.SourceFrames[state.ManifestFrame.SourceFrameIndex];
                }
                ActionFrame face = intervalWork.BaseState.ExpressionIndex == 1 ? work.Blink1
                    : intervalWork.BaseState.ExpressionIndex == 2 ? work.Blink2
                    : work.DefaultFace;
                AvatarRenderPrimitive[] intervalPrimitives = RenderState(
                    work.BodyFrames[intervalWork.BaseState.BodyFrame],
                    face,
                    effectActions);
                try
                {
                    List<PrimitiveGroup> groups = GroupPrimitives(intervalPrimitives, work);
                    var groupBitmaps = groups.Select(group => RenderPlane(work.Bounds, group.Primitives)).ToList();
                    var groupShas = new List<string>(groups.Count);
                    try
                    {
                        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                        {
                            PrimitiveGroup group = groups[groupIndex];
                            Bitmap bitmap = groupBitmaps[groupIndex];
                            byte[] png = EncodePng(bitmap);
                            string sha = ComputeSha256(png);
                            groupShas.Add(sha);
                            string directory;
                            if (group.Kind == AvatarRenderPrimitiveKind.Base)
                            {
                                directory = "depth/avatar/" + work.Manifest.Id;
                            }
                            else
                            {
                                string effectDirectory = group.Definition.Manifest.SlotIndex + "-" + group.Definition.Manifest.ItemId;
                                directory = "effects/" + effectDirectory + "/" + work.Manifest.Id;
                            }
                            string prefix = group.Kind == AvatarRenderPrimitiveKind.Base ? "avatar" : SanitizeToken(group.Branch);
                            string relativePath = directory + "/" + prefix + "-" + sha + ".png";
                            string cacheKey = (group.Kind == AvatarRenderPrimitiveKind.Base ? "base" : group.Definition.Id + "/" + group.Branch) + "/" + sha;
                            if (!planeAssetCache.TryGetValue(cacheKey, out string assetId))
                            {
                                ValidatePngRoundTrip(
                                    work.Manifest.Name,
                                    intervalWork.Start,
                                    groupIndex,
                                    bitmap,
                                    png);
                                assetId = SaveAssetBytes(stage, manifest, relativePath, png, work.Bounds.Width, work.Bounds.Height, sha);
                                planeAssetCache.Add(cacheKey, assetId);
                                planeAssetShaById.Add(assetId, sha);
                            }

                            SelectedEffectState selected = group.Kind == AvatarRenderPrimitiveKind.IndependentEffect
                                ? intervalWork.EffectStates.FirstOrDefault(state => state.Track.Definition.Id == group.Definition.Id && state.Track.Manifest.Branch == group.Branch)
                                : null;
                            int opacityStart = 255;
                            int opacityEnd = 255;
                            if (selected != null)
                            {
                                int a0 = ClampOpacity(group.Primitives[0].A0);
                                int a1 = ClampOpacity(group.Primitives[0].A1);
                                opacityStart = InterpolateOpacity(a0, a1, selected.LocalFrame, selected.ManifestFrame.DurationFrames);
                                // The manifest uses last-visible-frame opacity; AE places this value at duration - 1.
                                opacityEnd = InterpolateOpacity(
                                    a0,
                                    a1,
                                    selected.LocalFrame + Math.Max(0, intervalWork.Duration - 1),
                                    selected.ManifestFrame.DurationFrames);
                            }
                            interval.Planes.Add(new RtdPlane
                            {
                                AssetId = assetId,
                                Kind = group.Kind == AvatarRenderPrimitiveKind.Base ? "avatar" : "effect",
                                EffectId = group.Kind == AvatarRenderPrimitiveKind.Base ? null : group.Definition.Id,
                                Branch = group.Kind == AvatarRenderPrimitiveKind.Base ? null : group.Branch,
                                SourceKey = string.Join("|", group.Primitives.Select(primitive => primitive.SourceKey ?? string.Empty).Distinct()),
                                DrawOrder = groupIndex,
                                RawZ = string.Join("|", group.Primitives.Select(primitive => primitive.RawZ ?? (primitive.RawZIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)).Distinct()),
                                ResolvedZ = group.Primitives[0].ResolvedZ,
                                OpacityStart = opacityStart,
                                OpacityEnd = opacityEnd
                            });
                        }
                        PlanePixelDifference difference = ValidatePlaneRecomposition(
                            work.Manifest.Name,
                            intervalWork.Start,
                            work.Bounds,
                            intervalPrimitives,
                            groups,
                            groupBitmaps,
                            groupShas,
                            interval.Planes,
                            planeAssetShaById);
                        if (difference.MaxChannelDelta > 0)
                        {
                            planeRoundingIntervals++;
                            planeRoundingPixels += difference.DifferentPixelCount;
                            if (difference.MaxChannelDelta > maxPlaneRoundingDelta)
                            {
                                maxPlaneRoundingDelta = difference.MaxChannelDelta;
                                maxPlaneRoundingFrame = intervalWork.Start;
                                maxPlaneRoundingX = difference.X;
                                maxPlaneRoundingY = difference.Y;
                                maxPlaneExpectedArgb = difference.ExpectedArgb;
                                maxPlaneExpectedPremultipliedArgb = difference.ExpectedPremultipliedArgb;
                                maxPlaneActualArgb = difference.ActualArgb;
                                maxPlaneActualPremultipliedArgb = difference.ActualPremultipliedArgb;
                            }
                        }
                    }
                    finally
                    {
                        foreach (Bitmap bitmap in groupBitmaps) bitmap.Dispose();
                    }
                }
                finally
                {
                    DisposeOwnedPrimitives(intervalPrimitives);
                }
                work.Manifest.Intervals.Add(interval);
                if (ShouldReportIntervalProgress(intervalIndex, intervalCount))
                {
                    reportPhase?.Invoke("PNG "
                        + (intervalIndex + 1).ToString("N0", CultureInfo.CurrentCulture) + "/"
                        + intervalCount.ToString("N0", CultureInfo.CurrentCulture)
                        + ": depth/effect 렌더링 및 검증 중...");
                }
            }
            if (planeRoundingIntervals > 0)
            {
                string warning = string.Format(
                    CultureInfo.InvariantCulture,
                    "원본 primitive의 누락·중복·순서와 PNG 왕복 검증은 통과했지만, plane 재합성의 8-bit alpha 재그룹 반올림 차이가 {0:N0}개 interval에서 누적 {1:N0}회 발생했습니다. 최대 {2} LSB: frame {3}, ({4}, {5}), expected #{6:X8} (premul #{7:X8}), actual #{8:X8} (premul #{9:X8}).",
                    planeRoundingIntervals,
                    planeRoundingPixels,
                    maxPlaneRoundingDelta,
                    maxPlaneRoundingFrame,
                    maxPlaneRoundingX,
                    maxPlaneRoundingY,
                    maxPlaneExpectedArgb,
                    maxPlaneExpectedPremultipliedArgb,
                    maxPlaneActualArgb,
                    maxPlaneActualPremultipliedArgb);
                work.Manifest.Warnings.Add(warning);
                reportPhase?.Invoke("plane 재합성 품질 경고: 최대 "
                    + maxPlaneRoundingDelta.ToString(CultureInfo.InvariantCulture)
                    + " LSB (구조/PNG 검증 통과)");
            }
        }

        private static bool ShouldReportIntervalProgress(int index, int total)
        {
            if (total <= 0) return false;
            int completed = index + 1;
            int step = Math.Max(1, (total + 99) / 100);
            return completed == 1 || completed == total || completed % step == 0;
        }

        private List<PrimitiveGroup> GroupPrimitives(AvatarRenderPrimitive[] primitives, ActionWork work)
        {
            var groups = new List<PrimitiveGroup>();
            foreach (AvatarRenderPrimitive primitive in primitives)
            {
                EffectDefinition definition = null;
                string branch = null;
                if (primitive.Kind == AvatarRenderPrimitiveKind.IndependentEffect)
                {
                    definition = FindEffectDefinition(work, primitive);
                    if (definition == null)
                    {
                        throw new InvalidDataException("독립 이펙트 primitive의 소유자를 찾을 수 없습니다.");
                    }
                    branch = primitive.EffectBranch ?? GetEffectBranch(primitive.EffectSlot ?? definition.Manifest.SlotIndex);
                }

                PrimitiveGroup last = groups.Count == 0 ? null : groups[groups.Count - 1];
                bool append = last != null && last.Kind == primitive.Kind;
                if (append && primitive.Kind == AvatarRenderPrimitiveKind.IndependentEffect)
                {
                    append = last.Definition.Id == definition.Id
                        && string.Equals(last.Branch, branch, StringComparison.Ordinal)
                        && last.Primitives[0].A0 == primitive.A0
                        && last.Primitives[0].A1 == primitive.A1;
                }
                if (!append)
                {
                    last = new PrimitiveGroup(primitive.Kind, definition, branch);
                    groups.Add(last);
                }
                last.Primitives.Add(primitive);
            }
            return groups;
        }

        private EffectDefinition FindEffectDefinition(ActionWork work, AvatarRenderPrimitive primitive)
        {
            int slot = primitive.EffectSlot ?? -1;
            return work.Tracks.Select(track => track.Definition)
                .FirstOrDefault(definition => definition.Slots.Contains(slot)
                    && (!primitive.EffectItemId.HasValue || definition.Manifest.ItemId == primitive.EffectItemId.Value));
        }

        private bool TryGetEffectDefinition(int slot, out EffectDefinition definition)
        {
            definition = null;
            if (slot == AvatarCanvas.IndexChairLayer1 || slot == AvatarCanvas.IndexChairLayer2
                || slot == AvatarCanvas.IndexChairEffectLayer1 || slot == AvatarCanvas.IndexChairEffectLayer2)
            {
                return false;
            }
            if (slot < 0 || slot >= AvatarCanvas.LayerSlotLength || !avatar.IsPartEffectVisible(slot))
            {
                return false;
            }

            bool item501 = slot == AvatarCanvas.IndexEffectLayer1 || slot == AvatarCanvas.IndexEffectLayer2;
            int partSlot = item501 ? AvatarCanvas.IndexEffectLayer1 : slot;
            if (partSlot >= AvatarCanvas.PartLength) return false;
            AvatarPart part = avatar.Parts[partSlot];
            if (part == null || part.EffectNode == null || !part.ID.HasValue)
            {
                return false;
            }
            if (!item501 && (part == avatar.Taming || part == avatar.Saddle || part == avatar.Chair))
            {
                return false;
            }

            int canonicalSlot = item501 ? AvatarCanvas.IndexEffectLayer1 : slot;
            string kind = item501 ? "item501" : "itemEff";
            string id = "fx-" + kind + "-" + canonicalSlot + "-" + part.ID.Value;
            definition = new EffectDefinition
            {
                Id = id,
                Slots = item501
                    ? new[] { AvatarCanvas.IndexEffectLayer1, AvatarCanvas.IndexEffectLayer2 }
                    : new[] { slot },
                Manifest = new RtdEffect
                {
                    Id = id,
                    DisplayName = part.ID.Value + " (slot " + canonicalSlot + ")",
                    Kind = kind,
                    SlotIndex = canonicalSlot,
                    ItemId = part.ID.Value
                }
            };
            return true;
        }

        private string GetResolvedEffectAction(string actionName, int slot)
        {
            AvatarPart part;
            if (slot == AvatarCanvas.IndexEffectLayer1 || slot == AvatarCanvas.IndexEffectLayer2)
            {
                part = avatar.Effect;
                string branch = GetEffectBranch(slot);
                var root = part?.EffectNode?.FindNodeByPath(branch);
                return root?.FindNodeByPath(actionName) != null ? actionName : "default";
            }
            part = slot >= 0 && slot < AvatarCanvas.PartLength ? avatar.Parts[slot] : null;
            return part?.EffectNode?.FindNodeByPath(actionName) != null ? actionName : "default";
        }

        private static string GetEffectBranch(int slot)
        {
            return slot == AvatarCanvas.IndexEffectLayer2 ? "effect2" : "effect";
        }

        private AvatarRenderPrimitive[] RenderState(ActionFrame body, ActionFrame face, ActionFrame[] effects)
        {
            return avatar.CreateFramePrimitives(avatar.CreateFrame(body, face, null, effects));
        }

        private static Bitmap RenderPlane(Rectangle canvas, IEnumerable<AvatarRenderPrimitive> primitives)
        {
            var bitmap = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                foreach (AvatarRenderPrimitive primitive in primitives)
                {
                    graphics.DrawImage(primitive.Bitmap, primitive.Position.X - canvas.X, primitive.Position.Y - canvas.Y);
                }
            }
            return bitmap;
        }

        private string SaveAsset(string stage, RtdAvatarManifest manifest, string relativePath, Bitmap bitmap, string forcedAssetId)
        {
            using (bitmap)
            {
                byte[] png = EncodePng(bitmap);
                string sha = ComputeSha256(png);
                return SaveAssetBytes(stage, manifest, relativePath, png, bitmap.Width, bitmap.Height, sha, forcedAssetId);
            }
        }

        private string SaveAssetBytes(
            string stage,
            RtdAvatarManifest manifest,
            string relativePath,
            byte[] bytes,
            int width,
            int height,
            string sha,
            string forcedAssetId = null)
        {
            relativePath = relativePath.Replace('\\', '/');
            string path = Path.Combine(stage, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
            string assetId = forcedAssetId ?? "asset-" + nextAssetId++.ToString("D7", CultureInfo.InvariantCulture);
            manifest.Assets.Add(new RtdAsset
            {
                Id = assetId,
                Path = relativePath,
                Sha256 = sha,
                Width = width,
                Height = height
            });
            if (!manifest.ManagedFiles.Contains(relativePath, StringComparer.Ordinal)) manifest.ManagedFiles.Add(relativePath);
            return assetId;
        }

        private static byte[] EncodePng(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        private static string ComputeSha256(byte[] data)
        {
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string GetVisualKey(AvatarRenderPrimitive[] primitives)
        {
            Rectangle bounds = Rectangle.Empty;
            foreach (AvatarRenderPrimitive primitive in primitives)
            {
                Rectangle rect = new Rectangle(primitive.Position, primitive.Bitmap.Size);
                bounds = bounds.IsEmpty ? rect : Rectangle.Union(bounds, rect);
            }
            if (bounds.IsEmpty) return "empty";
            using (Bitmap bitmap = RenderPlane(bounds, primitives))
            {
                return ComputeSha256(EncodePng(bitmap));
            }
        }

        private static void ValidateStage(string stage, RtdAvatarManifest manifest)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RtdAsset asset in manifest.Assets)
            {
                if (!ids.Add(asset.Id)) throw new InvalidDataException("중복 asset id: " + asset.Id);
                if (!paths.Add(asset.Path)) throw new InvalidDataException("중복 asset path: " + asset.Path);
                if (Path.IsPathRooted(asset.Path) || asset.Path.IndexOf(':') >= 0 || asset.Path.Replace('\\', '/').Split('/').Any(part => part == ".."))
                {
                    throw new InvalidDataException("안전하지 않은 asset path: " + asset.Path);
                }
                string path = Path.Combine(stage, asset.Path.Replace('/', Path.DirectorySeparatorChar));
                byte[] data = File.ReadAllBytes(path);
                if (!string.Equals(asset.Sha256, ComputeSha256(data), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("PNG 해시 검증 실패: " + asset.Path);
                }
                using (var bitmap = new Bitmap(path))
                {
                    if (bitmap.Width != asset.Width || bitmap.Height != asset.Height)
                    {
                        throw new InvalidDataException("PNG 크기 검증 실패: " + asset.Path);
                    }
                }
            }

            foreach (RtdAction action in manifest.Actions)
            {
                foreach (RtdInterval interval in action.Intervals)
                {
                    if (interval.Planes.Select((plane, index) => plane.DrawOrder == index).Any(equal => !equal))
                    {
                        throw new InvalidDataException(action.Name + ": drawOrder가 연속적이지 않습니다.");
                    }
                    foreach (RtdPlane plane in interval.Planes)
                    {
                        if (!ids.Contains(plane.AssetId)) throw new InvalidDataException("존재하지 않는 plane asset: " + plane.AssetId);
                    }
                }
            }
        }

        private static void ValidatePngRoundTrip(
            string actionName,
            int startFrame,
            int planeIndex,
            Bitmap expected,
            byte[] png)
        {
            using (var stream = new MemoryStream(png, false))
            using (var actual = new Bitmap(stream))
            {
                EnsureSameBitmapSize(
                    actionName,
                    startFrame,
                    "plane " + planeIndex.ToString(CultureInfo.InvariantCulture) + " PNG round-trip",
                    expected,
                    actual);
                PlanePixelDifference difference = MeasurePlanePixelDifference(expected, actual);
                if (difference.MaxChannelDelta > 0)
                {
                    ThrowPlanePixelMismatch(
                        actionName,
                        startFrame,
                        "plane " + planeIndex.ToString(CultureInfo.InvariantCulture) + " PNG round-trip",
                        difference);
                }
            }
        }

        private static PlanePixelDifference ValidatePlaneRecomposition(
            string actionName,
            int startFrame,
            Rectangle canvas,
            AvatarRenderPrimitive[] originalPrimitives,
            IList<PrimitiveGroup> groups,
            IList<Bitmap> planeBitmaps,
            IList<string> planeShas,
            IList<RtdPlane> planes,
            IDictionary<string, string> assetShaById)
        {
            ValidatePlaneStructure(
                actionName,
                startFrame,
                canvas,
                originalPrimitives,
                groups,
                planeBitmaps,
                planeShas,
                planes,
                assetShaById);

            using (Bitmap expected = RenderPlane(canvas, originalPrimitives))
            {
                if (planeBitmaps.Count == 0)
                {
                    using (var actual = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format32bppArgb))
                    {
                        return ValidateExactPlanePixels(actionName, startFrame, "empty plane recomposition", expected, actual);
                    }
                }

                if (planeBitmaps.Count == 1)
                {
                    return ValidateExactPlanePixels(actionName, startFrame, "single-plane recomposition", expected, planeBitmaps[0]);
                }

                using (Bitmap actual = planeBitmaps[0].Clone(
                    new Rectangle(0, 0, canvas.Width, canvas.Height),
                    PixelFormat.Format32bppArgb))
                {
                    using (Graphics graphics = Graphics.FromImage(actual))
                    {
                        for (int index = 1; index < planeBitmaps.Count; index++)
                        {
                            graphics.DrawImageUnscaled(planeBitmaps[index], 0, 0);
                        }
                    }

                    EnsureSameBitmapSize(actionName, startFrame, "multi-plane recomposition", expected, actual);
                    return MeasurePlanePixelDifference(expected, actual);
                }
            }
        }

        private static void ValidatePlaneStructure(
            string actionName,
            int startFrame,
            Rectangle canvas,
            AvatarRenderPrimitive[] originalPrimitives,
            IList<PrimitiveGroup> groups,
            IList<Bitmap> planeBitmaps,
            IList<string> planeShas,
            IList<RtdPlane> planes,
            IDictionary<string, string> assetShaById)
        {
            if (groups.Count != planeBitmaps.Count
                || groups.Count != planeShas.Count
                || groups.Count != planes.Count)
            {
                throw new InvalidDataException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: frame {1} plane structure count mismatch; groups {2}, bitmaps {3}, hashes {4}, manifest planes {5}.",
                    actionName,
                    startFrame,
                    groups.Count,
                    planeBitmaps.Count,
                    planeShas.Count,
                    planes.Count));
            }

            int primitiveIndex = 0;
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                PrimitiveGroup group = groups[groupIndex];
                Bitmap bitmap = planeBitmaps[groupIndex];
                string planeSha = planeShas[groupIndex];
                RtdPlane plane = planes[groupIndex];
                if (group.Primitives.Count == 0)
                {
                    throw new InvalidDataException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: frame {1} plane group {2} is empty.",
                        actionName,
                        startFrame,
                        groupIndex));
                }
                if (bitmap.Width != canvas.Width || bitmap.Height != canvas.Height)
                {
                    throw new InvalidDataException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: frame {1} plane {2} size mismatch; expected {3}x{4}, actual {5}x{6}.",
                        actionName,
                        startFrame,
                        groupIndex,
                        canvas.Width,
                        canvas.Height,
                        bitmap.Width,
                        bitmap.Height));
                }
                if (plane.DrawOrder != groupIndex)
                {
                    throw new InvalidDataException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: frame {1} plane {2} drawOrder mismatch; actual {3}.",
                        actionName,
                        startFrame,
                        groupIndex,
                        plane.DrawOrder));
                }

                string expectedKind = group.Kind == AvatarRenderPrimitiveKind.Base ? "avatar" : "effect";
                string expectedEffectId = group.Kind == AvatarRenderPrimitiveKind.Base ? null : group.Definition?.Id;
                string expectedBranch = group.Kind == AvatarRenderPrimitiveKind.Base ? null : group.Branch;
                string expectedSourceKey = string.Join("|", group.Primitives
                    .Select(primitive => primitive.SourceKey ?? string.Empty)
                    .Distinct());
                string expectedRawZ = string.Join("|", group.Primitives
                    .Select(primitive => primitive.RawZ
                        ?? (primitive.RawZIndex?.ToString(CultureInfo.InvariantCulture) ?? string.Empty))
                    .Distinct());
                int expectedResolvedZ = group.Primitives[0].ResolvedZ;
                if (string.IsNullOrEmpty(plane.AssetId)
                    || !assetShaById.TryGetValue(plane.AssetId, out string linkedSha)
                    || !string.Equals(linkedSha, planeSha, StringComparison.Ordinal)
                    || !string.Equals(plane.Kind, expectedKind, StringComparison.Ordinal)
                    || !string.Equals(plane.EffectId, expectedEffectId, StringComparison.Ordinal)
                    || !string.Equals(plane.Branch, expectedBranch, StringComparison.Ordinal)
                    || !string.Equals(plane.SourceKey, expectedSourceKey, StringComparison.Ordinal)
                    || !string.Equals(plane.RawZ, expectedRawZ, StringComparison.Ordinal)
                    || plane.ResolvedZ != expectedResolvedZ)
                {
                    throw new InvalidDataException(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}: frame {1} plane {2} manifest linkage mismatch.",
                        actionName,
                        startFrame,
                        groupIndex));
                }

                foreach (AvatarRenderPrimitive primitive in group.Primitives)
                {
                    if (primitiveIndex >= originalPrimitives.Length
                        || !object.ReferenceEquals(primitive, originalPrimitives[primitiveIndex]))
                    {
                        throw new InvalidDataException(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}: frame {1} primitive sequence mismatch at group {2}, primitive {3}.",
                            actionName,
                            startFrame,
                            groupIndex,
                            primitiveIndex));
                    }
                    if (primitive.DrawOrdinal != primitiveIndex || primitive.Kind != group.Kind)
                    {
                        throw new InvalidDataException(string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}: frame {1} primitive provenance mismatch at ordinal {2}.",
                            actionName,
                            startFrame,
                            primitiveIndex));
                    }
                    primitiveIndex++;
                }
            }

            if (primitiveIndex != originalPrimitives.Length)
            {
                throw new InvalidDataException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: frame {1} primitive count mismatch; grouped {2}, original {3}.",
                    actionName,
                    startFrame,
                    primitiveIndex,
                    originalPrimitives.Length));
            }
        }

        private static PlanePixelDifference ValidateExactPlanePixels(
            string actionName,
            int startFrame,
            string context,
            Bitmap expected,
            Bitmap actual)
        {
            EnsureSameBitmapSize(actionName, startFrame, context, expected, actual);
            PlanePixelDifference difference = MeasurePlanePixelDifference(expected, actual);
            if (difference.MaxChannelDelta > 0)
            {
                ThrowPlanePixelMismatch(actionName, startFrame, context, difference);
            }
            return difference;
        }

        private static void EnsureSameBitmapSize(
            string actionName,
            int startFrame,
            string context,
            Bitmap expected,
            Bitmap actual)
        {
            if (expected.Width == actual.Width && expected.Height == actual.Height) return;
            throw new InvalidDataException(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: frame {1} {2} size mismatch; expected {3}x{4}, actual {5}x{6}.",
                actionName,
                startFrame,
                context,
                expected.Width,
                expected.Height,
                actual.Width,
                actual.Height));
        }

        private static void ThrowPlanePixelMismatch(
            string actionName,
            int startFrame,
            string context,
            PlanePixelDifference difference)
        {
            throw new InvalidDataException(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: frame {1} {2} mismatch at ({3}, {4}); 8-bit premultiplied channel delta {5}; expected #{6:X8} (premul #{7:X8}), actual #{8:X8} (premul #{9:X8}).",
                actionName,
                startFrame,
                context,
                difference.X,
                difference.Y,
                difference.MaxChannelDelta,
                difference.ExpectedArgb,
                difference.ExpectedPremultipliedArgb,
                difference.ActualArgb,
                difference.ActualPremultipliedArgb));
        }

        private static PlanePixelDifference MeasurePlanePixelDifference(Bitmap left, Bitmap right)
        {
            var result = new PlanePixelDifference();
            var rect = new Rectangle(0, 0, left.Width, left.Height);
            BitmapData leftData = null;
            BitmapData rightData = null;
            try
            {
                leftData = left.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                rightData = right.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int rowLength = checked(left.Width * 4);
                var leftRow = new byte[rowLength];
                var rightRow = new byte[rowLength];
                for (int y = 0; y < left.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(leftData.Scan0, y * leftData.Stride), leftRow, 0, rowLength);
                    Marshal.Copy(IntPtr.Add(rightData.Scan0, y * rightData.Stride), rightRow, 0, rowLength);
                    for (int x = 0; x < left.Width; x++)
                    {
                        int index = x * 4;
                        if (leftRow[index] == rightRow[index]
                            && leftRow[index + 1] == rightRow[index + 1]
                            && leftRow[index + 2] == rightRow[index + 2]
                            && leftRow[index + 3] == rightRow[index + 3])
                        {
                            continue;
                        }

                        byte leftAlpha = leftRow[index + 3];
                        byte rightAlpha = rightRow[index + 3];
                        int delta = Math.Max(
                            Math.Abs(leftAlpha - rightAlpha),
                            Math.Max(
                                Math.Abs(PremultiplyChannel(leftRow[index], leftAlpha) - PremultiplyChannel(rightRow[index], rightAlpha)),
                                Math.Max(
                                    Math.Abs(PremultiplyChannel(leftRow[index + 1], leftAlpha) - PremultiplyChannel(rightRow[index + 1], rightAlpha)),
                                    Math.Abs(PremultiplyChannel(leftRow[index + 2], leftAlpha) - PremultiplyChannel(rightRow[index + 2], rightAlpha)))));
                        if (delta == 0) continue;

                        result.DifferentPixelCount++;
                        if (delta > result.MaxChannelDelta)
                        {
                            result.MaxChannelDelta = delta;
                            result.X = x;
                            result.Y = y;
                            result.ExpectedArgb = ToArgb(leftRow, index);
                            result.ExpectedPremultipliedArgb = ToPremultipliedArgb(leftRow, index);
                            result.ActualArgb = ToArgb(rightRow, index);
                            result.ActualPremultipliedArgb = ToPremultipliedArgb(rightRow, index);
                        }
                    }
                }
                return result;
            }
            finally
            {
                if (rightData != null) right.UnlockBits(rightData);
                if (leftData != null) left.UnlockBits(leftData);
            }
        }

        private static byte PremultiplyChannel(byte channel, byte alpha)
        {
            return (byte)((channel * alpha + 127) / 255);
        }

        private static uint ToArgb(byte[] row, int index)
        {
            return ((uint)row[index + 3] << 24)
                | ((uint)row[index + 2] << 16)
                | ((uint)row[index + 1] << 8)
                | row[index];
        }

        private static uint ToPremultipliedArgb(byte[] row, int index)
        {
            byte alpha = row[index + 3];
            return ((uint)alpha << 24)
                | ((uint)PremultiplyChannel(row[index + 2], alpha) << 16)
                | ((uint)PremultiplyChannel(row[index + 1], alpha) << 8)
                | PremultiplyChannel(row[index], alpha);
        }

        private static void IncludeBounds(ActionWork work, AvatarRenderPrimitive[] primitives)
        {
            foreach (AvatarRenderPrimitive primitive in primitives)
            {
                Rectangle rect = new Rectangle(primitive.Position, primitive.Bitmap.Size);
                work.Bounds = work.Bounds.IsEmpty ? rect : Rectangle.Union(work.Bounds, rect);
            }
        }

        private static void DisposeOwnedPrimitives(IEnumerable<AvatarRenderPrimitive> primitives)
        {
            if (primitives == null) return;
            var disposed = new HashSet<Bitmap>();
            foreach (AvatarRenderPrimitive primitive in primitives)
            {
                if (primitive.OwnsBitmap && primitive.Bitmap != null && disposed.Add(primitive.Bitmap))
                {
                    primitive.Bitmap.Dispose();
                }
            }
        }

        private static void AddRepeatedBoundaries(SortedSet<int> boundaries, int master, IEnumerable<int> durations, int cycle)
        {
            int[] values = durations.ToArray();
            for (int cycleStart = 0; cycleStart < master; cycleStart += cycle)
            {
                int cursor = cycleStart;
                foreach (int duration in values)
                {
                    cursor += duration;
                    if (cursor > 0 && cursor < master) boundaries.Add(cursor);
                }
            }
        }

        private static T SelectTimelineFrame<T>(IList<T> frames, int cycleOffset, out int localFrame) where T : ITimelineFrame
        {
            int cursor = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                int end = cursor + frames[i].DurationFrames;
                if (cycleOffset < end)
                {
                    localFrame = cycleOffset - cursor;
                    return frames[i];
                }
                cursor = end;
            }
            localFrame = 0;
            return frames[frames.Count - 1];
        }

        private static RtdEffectFrame SelectTimelineFrame(IList<RtdEffectFrame> frames, int cycleOffset, out int localFrame)
        {
            int cursor = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                int end = cursor + frames[i].DurationFrames;
                if (cycleOffset < end)
                {
                    localFrame = cycleOffset - cursor;
                    return frames[i];
                }
                cursor = end;
            }
            localFrame = 0;
            return frames[frames.Count - 1];
        }

        private static int InterpolateOpacity(int a0, int a1, int localFrame, int duration)
        {
            if (duration <= 0) return ClampOpacity(a1);
            int clamped = Math.Max(0, Math.Min(duration, localFrame));
            return ClampOpacity((int)Math.Round(a0 + (a1 - a0) * (clamped / (double)duration), MidpointRounding.AwayFromZero));
        }

        private static int ClampOpacity(int value)
        {
            return Math.Max(0, Math.Min(255, value));
        }

        private static long GreatestCommonDivisor(long left, long right)
        {
            while (right != 0)
            {
                long next = left % right;
                left = right;
                right = next;
            }
            return Math.Abs(left);
        }

        private static uint Fnv1a(string value)
        {
            uint hash = 2166136261;
            foreach (byte item in Encoding.UTF8.GetBytes(value ?? string.Empty))
            {
                hash ^= item;
                hash *= 16777619;
            }
            return hash;
        }

        private static bool IsFourStepAction(string actionName)
        {
            return actionName.StartsWith("stand", StringComparison.OrdinalIgnoreCase)
                || actionName.StartsWith("walk", StringComparison.OrdinalIgnoreCase);
        }

        private static string BasePathKey(int bodyFrame, int expressionIndex)
        {
            return bodyFrame + ":" + expressionIndex;
        }

        private static string SanitizeToken(string value)
        {
            return new FileTokenAllocator().Allocate(value);
        }

        private interface ITimelineFrame
        {
            int DurationFrames { get; }
        }

        private sealed class BaseTimelineFrame : ITimelineFrame
        {
            public BaseTimelineFrame(int bodyFrame, string expression, int expressionIndex, int durationFrames)
            {
                BodyFrame = bodyFrame;
                Expression = expression;
                ExpressionIndex = expressionIndex;
                DurationFrames = durationFrames;
            }
            public int BodyFrame { get; }
            public string Expression { get; }
            public int ExpressionIndex { get; }
            public int DurationFrames { get; }
        }

        private sealed class BaseVariants
        {
            public AvatarRenderPrimitive[] Default { get; set; }
            public AvatarRenderPrimitive[] Blink1 { get; set; }
            public AvatarRenderPrimitive[] Blink2 { get; set; }
            public bool HasBlink { get; set; }
        }

        private sealed class EffectDefinition
        {
            public string Id { get; set; }
            public int[] Slots { get; set; }
            public RtdEffect Manifest { get; set; }
        }

        private sealed class EffectTrackWork
        {
            public EffectTrackWork(int slot, EffectDefinition definition, ActionFrame[] sourceFrames, RtdEffectTrack manifest)
            {
                Slot = slot;
                Definition = definition;
                SourceFrames = sourceFrames;
                Manifest = manifest;
            }
            public int Slot { get; }
            public EffectDefinition Definition { get; }
            public ActionFrame[] SourceFrames { get; }
            public RtdEffectTrack Manifest { get; }
        }

        private sealed class SelectedEffectState
        {
            public SelectedEffectState(EffectTrackWork track, RtdEffectFrame manifestFrame, int localFrame)
            {
                Track = track;
                ManifestFrame = manifestFrame;
                LocalFrame = localFrame;
            }
            public EffectTrackWork Track { get; }
            public RtdEffectFrame ManifestFrame { get; }
            public int LocalFrame { get; }
        }

        private sealed class IntervalWork
        {
            public IntervalWork(int start, int duration, BaseTimelineFrame baseState, List<SelectedEffectState> effectStates)
            {
                Start = start;
                Duration = duration;
                BaseState = baseState;
                EffectStates = effectStates;
            }
            public int Start { get; }
            public int Duration { get; }
            public BaseTimelineFrame BaseState { get; }
            public List<SelectedEffectState> EffectStates { get; }
        }

        private sealed class PrimitiveGroup
        {
            public PrimitiveGroup(AvatarRenderPrimitiveKind kind, EffectDefinition definition, string branch)
            {
                Kind = kind;
                Definition = definition;
                Branch = branch;
            }
            public AvatarRenderPrimitiveKind Kind { get; }
            public EffectDefinition Definition { get; }
            public string Branch { get; }
            public List<AvatarRenderPrimitive> Primitives { get; } = new List<AvatarRenderPrimitive>();
        }

        private struct PlanePixelDifference
        {
            public int MaxChannelDelta;
            public long DifferentPixelCount;
            public int X;
            public int Y;
            public uint ExpectedArgb;
            public uint ExpectedPremultipliedArgb;
            public uint ActualArgb;
            public uint ActualPremultipliedArgb;
        }

        private sealed class ActionWork
        {
            public RtdAction Manifest { get; set; }
            public ActionFrame[] BodyFrames { get; set; }
            public ActionFrame DefaultFace { get; set; }
            public ActionFrame Blink1 { get; set; }
            public ActionFrame Blink2 { get; set; }
            public List<BaseVariants> BaseVariants { get; } = new List<BaseVariants>();
            public List<BaseTimelineFrame> BaseTimeline { get; } = new List<BaseTimelineFrame>();
            public List<EffectTrackWork> Tracks { get; } = new List<EffectTrackWork>();
            public List<IntervalWork> IntervalWorks { get; } = new List<IntervalWork>();
            public Rectangle Bounds { get; set; }
            public bool BlinkOmitted { get; set; }

            public void DisposeOwnedBitmaps()
            {
                var disposed = new HashSet<Bitmap>();
                Action<AvatarRenderPrimitive[]> dispose = primitives =>
                {
                    if (primitives == null) return;
                    foreach (AvatarRenderPrimitive primitive in primitives)
                    {
                        if (primitive.OwnsBitmap && primitive.Bitmap != null && disposed.Add(primitive.Bitmap))
                        {
                            primitive.Bitmap.Dispose();
                        }
                    }
                };
                foreach (BaseVariants variants in BaseVariants)
                {
                    dispose(variants.Default);
                    dispose(variants.Blink1);
                    dispose(variants.Blink2);
                }
            }
        }

        private sealed class FileTokenAllocator
        {
            private readonly HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public string Allocate(string original)
            {
                string normalized = (original ?? string.Empty).Normalize(NormalizationForm.FormC);
                var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
                var result = new StringBuilder(normalized.Length);
                bool lastDot = false;
                foreach (char ch in normalized)
                {
                    bool replace = char.IsControl(ch) || invalid.Contains(ch) || ch == '_' || ch == '(' || ch == ')';
                    char output = replace ? '.' : ch;
                    if (output == '.' && lastDot) continue;
                    result.Append(output);
                    lastDot = output == '.';
                }
                string token = result.ToString().Trim(' ', '.');
                if (token.Length == 0) token = "action";
                string candidate = token;
                int suffix = 2;
                while (!used.Add(candidate)) candidate = token + "-" + suffix++;
                return candidate;
            }
        }
    }

    internal sealed class RtdExportEstimate
    {
        public RtdExportEstimate(
            int actionCount,
            int skippedActionCount,
            int effectTrackCount,
            long estimatedStateCount,
            int maxMasterFrames,
            long totalMasterFrames,
            int fallbackActionCount,
            int blinkOmittedActionCount)
        {
            ActionCount = actionCount;
            SkippedActionCount = skippedActionCount;
            EffectTrackCount = effectTrackCount;
            EstimatedStateCount = estimatedStateCount;
            MaxMasterFrames = maxMasterFrames;
            TotalMasterFrames = totalMasterFrames;
            FallbackActionCount = fallbackActionCount;
            BlinkOmittedActionCount = blinkOmittedActionCount;
        }
        public int ActionCount { get; }
        public int SkippedActionCount { get; }
        public int EffectTrackCount { get; }
        public long EstimatedStateCount { get; }
        public int MaxMasterFrames { get; }
        public long TotalMasterFrames { get; }
        public int FallbackActionCount { get; }
        public int BlinkOmittedActionCount { get; }
    }

    internal sealed class RtdExportResult
    {
        public string OutputDirectory { get; set; }
        public int ActionCount { get; set; }
        public int SkippedActions { get; set; }
        public int EffectTrackCount { get; set; }
        public int AssetCount { get; set; }
        public int WarningCount { get; set; }
        public int BlinkOmittedActionCount { get; set; }
        public int LongestFallbackActionCount { get; set; }
    }
}
