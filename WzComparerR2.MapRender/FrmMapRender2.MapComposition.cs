using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using WzComparerR2.MapRender.Config;
using WzComparerR2.MapRender.Export;
using WzComparerR2.MapRender.UI;
using IE = System.Collections.IEnumerator;

namespace WzComparerR2.MapRender
{
    public partial class FrmMapRender2
    {
        private const int AfterEffectsMaximumCompositionDimension = 30000;
        private const double AfterEffectsMaximumDurationSeconds = 3d * 60d * 60d;

        private CancellationTokenSource compositionExportCancellation;
        private CompositionExportProgressForm compositionExportProgress;
        private string compositionCancellationPath;
        private bool compositionExportRunning;

        private void UIOption_BrowseAepPath(object sender, EventArgs e)
        {
            var window = sender as UIOptions;
            var model = window?.DataContext as UIOptionsDataModel;
            if (model == null)
            {
                return;
            }

            using (var dialog = new System.Windows.Forms.SaveFileDialog())
            {
                dialog.Title = "After Effects 프로젝트 저장 위치";
                dialog.Filter = "After Effects Project (*.aep)|*.aep";
                dialog.DefaultExt = "aep";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.RestoreDirectory = true;

                try
                {
                    var currentPath = Path.GetFullPath(model.CompositionAepPath ?? string.Empty);
                    dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
                    dialog.FileName = Path.GetFileName(currentPath);
                }
                catch (Exception)
                {
                    dialog.FileName = GetDefaultCompositionFileName();
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    model.CompositionAepPath = dialog.FileName;
                    model.CompositionStatus = string.Empty;
                }
            }
        }

        private void UIOption_CreateAep(object sender, EventArgs e)
        {
            var window = sender as UIOptions;
            var model = window?.DataContext as UIOptionsDataModel;
            if (model == null)
            {
                window?.EnableButtons();
                return;
            }

            BeginCompositionExport(window, model);
        }

        private void UIOption_CancelAep(object sender, EventArgs e)
        {
            var window = sender as UIOptions;
            var model = window?.DataContext as UIOptionsDataModel;
            if (!compositionExportRunning)
            {
                if (model != null)
                {
                    model.CompositionStatus = "진행 중인 AEP 생성 작업이 없습니다.";
                }
                return;
            }

            CancelCompositionExport();
            if (model != null)
            {
                model.CompositionStatus = "취소 요청을 처리하고 있습니다...";
            }
        }

        private void BeginCompositionExportFromCurrentSettings()
        {
            var model = new UIOptionsDataModel();
            LoadCompositionOptionData(model);
            BeginCompositionExport(null, model);
        }

        private void BeginCompositionExport(UIOptions window, UIOptionsDataModel model)
        {
            if (compositionExportRunning)
            {
                SetCompositionStatus(window, model, "이미 AEP를 생성하고 있습니다.", true);
                window?.EnableButtons();
                return;
            }

            if (mapData == null)
            {
                SetCompositionStatus(window, model, "먼저 맵을 불러와 주세요.", true);
                window?.EnableButtons();
                return;
            }

            if (!TryBuildCompositionRequest(model, out var request, out var validationError))
            {
                SetCompositionStatus(window, model, validationError, true);
                window?.EnableButtons();
                return;
            }

            var creatorPath = LocateAfterEffectTemplateCreator();
            if (creatorPath == null)
            {
                creatorPath = PromptForExecutable(
                    "AfterEffectTemplateCreator 실행 파일을 선택하세요.",
                    "AfterEffectTemplateCreator.exe|AfterEffectTemplateCreator.exe|실행 파일 (*.exe)|*.exe");
            }
            if (creatorPath == null)
            {
                SetCompositionStatus(window, model, "AfterEffectTemplateCreator 실행 파일을 찾지 못했습니다.", true);
                window?.EnableButtons();
                return;
            }

            var afterFxPath = LocateAfterFx();
            if (afterFxPath == null)
            {
                afterFxPath = PromptForExecutable(
                    "After Effects의 AfterFX.com을 선택하세요.",
                    "After Effects Command (AfterFX.com)|AfterFX.com|COM 실행 파일 (*.com)|*.com");
            }
            if (afterFxPath == null)
            {
                SetCompositionStatus(window, model, "AfterFX.com을 찾지 못했습니다. After Effects 설치를 확인해 주세요.", true);
                window?.EnableButtons();
                return;
            }

            SaveCompositionSettings(model, creatorPath, afterFxPath);
            compositionExportCancellation?.Dispose();
            compositionExportCancellation = new CancellationTokenSource();
            compositionExportProgress?.Dispose();
            compositionExportProgress = new CompositionExportProgressForm(
                CancelCompositionExport);
            compositionExportProgress.Show("맵 에셋과 타임라인을 내보내고 있습니다...");
            compositionExportRunning = true;
            SetCompositionStatus(window, model, "맵 자산과 타임라인을 내보내고 있습니다...", false);
            cm.StartCoroutine(CreateCompositionCoroutine(
                window,
                model,
                request,
                creatorPath,
                afterFxPath,
                compositionExportCancellation.Token));
        }

        private IE CreateCompositionCoroutine(
            UIOptions window,
            UIOptionsDataModel model,
            MapCompositionRequest request,
            string creatorPath,
            string afterFxPath,
            CancellationToken cancellationToken)
        {
            MapCompositionRunContext context;
            try
            {
                context = PrepareCompositionRun(request, creatorPath, afterFxPath, cancellationToken);
            }
            catch (Exception ex)
            {
                FinishCompositionExport(window, model, null, ex);
                yield break;
            }

            compositionCancellationPath = context.CancellationPath;
            SetCompositionStatus(window, model, "After Effects 프로젝트를 생성하고 있습니다...", false);
            var runTask = Task.Run(
                () => RunMapCompositionHeadless(
                    creatorPath,
                    context.JobPath,
                    request.OutputAepPath,
                    context.ResultPath,
                    context.LogPath,
                    context.CancellationPath,
                    cancellationToken),
                CancellationToken.None);
            yield return new WaitTaskCompletedCoroutine(runTask);

            MapCompositionProcessResult processResult = null;
            Exception failure = null;
            if (runTask.IsCanceled)
            {
                failure = new OperationCanceledException("AEP 생성이 취소되었습니다.", cancellationToken);
            }
            else if (runTask.IsFaulted)
            {
                failure = runTask.Exception?.GetBaseException() ?? new InvalidOperationException("AEP 생성 프로세스가 실패했습니다.");
            }
            else
            {
                processResult = runTask.Result;
            }

            if (failure == null)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context.Transaction.Commit();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            if (failure != null)
            {
                try
                {
                    context.Transaction.Rollback();
                }
                catch (Exception rollbackException)
                {
                    failure = new AggregateException(
                        "AEP 생성 실패 후 기존 AEP/map sidecar 복구에도 실패했습니다.",
                        failure,
                        rollbackException);
                }
            }

            FinishCompositionExport(window, model, processResult, failure, context.ExportResult);
        }

        private MapCompositionRunContext PrepareCompositionRun(
            MapCompositionRequest request,
            string creatorPath,
            string afterFxPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transaction = new MapCompositionTransaction(request.OutputAepPath);
            try
            {
                transaction.Begin();

                var cameraScale = renderEnv.Camera.Scale;
                var options = new MapCompositionExportOptions
                {
                    FrameRate = request.FrameRate,
                    DurationSeconds = request.DurationSeconds,
                    WorldRect = request.WorldRect,
                    ViewportWidth = request.ViewportWidth,
                    ViewportHeight = request.ViewportHeight,
                    CameraCenterX = cameraScale == 0 ? renderEnv.Camera.Center.X : renderEnv.Camera.Center.X / cameraScale,
                    CameraCenterY = cameraScale == 0 ? renderEnv.Camera.Center.Y : renderEnv.Camera.Center.Y / cameraScale,
                    CameraScale = cameraScale,
                    RenderCameraCenterX = renderEnv.Camera.Center.X,
                    RenderCameraCenterY = renderEnv.Camera.Center.Y,
                    DisplayMode = renderEnv.Camera.DisplayMode,
                    DisplayName = GetMapDisplayName(),
                    Visibility = patchVisibility,
                    SpineBaker = CreateSpineSequenceBaker()
                };

                var exporter = new MapCompositionExporter();
                var exportResult = exporter.Export(
                    mapData,
                    transaction.MapDirectory,
                    options,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                var jobPath = Path.Combine(transaction.MapDirectory, "map-job.json");
                var resultPath = Path.Combine(transaction.MapDirectory, "map-result.json");
                var logPath = Path.Combine(transaction.MapDirectory, "afterfx-map.log");
                var cancellationPath = Path.Combine(transaction.MapDirectory, "map-cancel.requested");
                if (File.Exists(cancellationPath)) File.Delete(cancellationPath);
                var job = new MapCompositionJobDocument
                {
                    Schema = "wzcomparer-map-job",
                    Version = 1,
                    ManifestPath = Path.GetFullPath(exportResult.ManifestPath),
                    TemporaryAepPath = transaction.TemporaryAepPath,
                    OutputAepPath = transaction.OutputAepPath,
                    AfterFxPath = Path.GetFullPath(afterFxPath),
                    CompPrefix = request.CompositionPrefix,
                    LogPath = Path.GetFullPath(logPath),
                    ResultPath = Path.GetFullPath(resultPath),
                    CancellationPath = Path.GetFullPath(cancellationPath)
                };

                WriteJson(jobPath, job);
                return new MapCompositionRunContext(
                    transaction,
                    exportResult,
                    jobPath,
                    resultPath,
                    logPath,
                    cancellationPath);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private IMapSpineSequenceBaker CreateSpineSequenceBaker()
        {
            return MapRenderSpineSequenceBaker.TryCreate(GraphicsDevice, Services, out var baker)
                ? baker
                : null;
        }

        private static MapCompositionProcessResult RunMapCompositionHeadless(
            string creatorPath,
            string jobPath,
            string expectedAepPath,
            string expectedResultPath,
            string expectedLogPath,
            string cancellationPath,
            CancellationToken cancellationToken)
        {
            var standardOutput = new StringBuilder();
            var standardError = new StringBuilder();
            var startInfo = new ProcessStartInfo
            {
                FileName = creatorPath,
                Arguments = "--map-job " + QuoteCommandLineArgument(Path.GetFullPath(jobPath)),
                WorkingDirectory = Path.GetDirectoryName(creatorPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true })
            {
                process.OutputDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        lock (standardOutput) standardOutput.AppendLine(args.Data);
                    }
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        lock (standardError) standardError.AppendLine(args.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("AfterEffectTemplateCreator 프로세스를 시작하지 못했습니다.");
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                while (!process.WaitForExit(250))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        var requestedGracefully = TryWriteCancellationRequest(cancellationPath);
                        if (!requestedGracefully || !process.WaitForExit(5000))
                        {
                            TryKillProcessTree(process);
                            process.WaitForExit(5000);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                process.WaitForExit();
                cancellationToken.ThrowIfCancellationRequested();

                if (!File.Exists(expectedResultPath))
                {
                    throw new InvalidDataException(
                        "AfterEffectTemplateCreator가 map-result.json을 만들지 않았습니다." +
                        FormatProcessOutput(standardOutput, standardError));
                }

                var result = ReadJson<MapCompositionResultDocument>(expectedResultPath);
                if (process.ExitCode != 0 || result == null || !result.Success)
                {
                    var message = result?.Error;
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        message = $"AfterEffectTemplateCreator가 종료 코드 {process.ExitCode}(으)로 실패했습니다.";
                    }
                    throw new InvalidOperationException(message + FormatProcessOutput(standardOutput, standardError));
                }

                if (!string.Equals(result.Schema, "wzcomparer-map-result", StringComparison.Ordinal) || result.Version != 1)
                {
                    throw new InvalidDataException("지원하지 않는 map-result.json 계약입니다.");
                }
                if (string.IsNullOrWhiteSpace(result.OutputAepPath) ||
                    !string.Equals(
                        Path.GetFullPath(result.OutputAepPath),
                        Path.GetFullPath(expectedAepPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("map-result.json의 최종 AEP 경로가 요청과 다릅니다.");
                }
                if (!File.Exists(expectedAepPath) || new FileInfo(expectedAepPath).Length == 0)
                {
                    throw new InvalidDataException("결과 JSON은 성공이지만 최종 AEP가 없거나 비어 있습니다.");
                }
                if (string.IsNullOrWhiteSpace(result.LogPath) ||
                    !string.Equals(
                        Path.GetFullPath(result.LogPath),
                        Path.GetFullPath(expectedLogPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(expectedLogPath))
                {
                    throw new InvalidDataException("map-result.json의 AfterFX 로그 경로가 없거나 요청과 다릅니다.");
                }

                return new MapCompositionProcessResult(
                    Path.GetFullPath(expectedAepPath),
                    result.LogPath,
                    result.Warnings ?? Array.Empty<string>());
            }
        }

        private void FinishCompositionExport(
            UIOptions window,
            UIOptionsDataModel model,
            MapCompositionProcessResult processResult,
            Exception failure,
            MapCompositionExportResult exportResult = null)
        {
            compositionExportRunning = false;
            compositionExportProgress?.Dispose();
            compositionExportProgress = null;
            compositionCancellationPath = null;
            compositionExportCancellation?.Dispose();
            compositionExportCancellation = null;
            window?.EnableButtons();

            if (failure != null)
            {
                var message = failure is OperationCanceledException
                    ? "AEP 생성을 취소했습니다. 기존 AEP와 map sidecar를 복구했습니다."
                    : "AEP 생성 실패: " + failure.Message;
                SetCompositionStatus(window, model, message, true);
                return;
            }

            var warningCount = processResult?.Warnings?.Length ?? exportResult?.WarningCount ?? 0;
            var success = $"AEP가 만들어졌습니다: {processResult.OutputAepPath}";
            if (warningCount > 0)
            {
                success += $" (경고 {warningCount}개, map-result.json 확인)";
            }
            SetCompositionStatus(window, model, success, true);
        }

        private void CancelCompositionExport()
        {
            compositionExportCancellation?.Cancel();
            if (!string.IsNullOrWhiteSpace(compositionCancellationPath))
            {
                TryWriteCancellationRequest(compositionCancellationPath);
            }
        }

        private void SetCompositionStatus(UIOptions window, UIOptionsDataModel model, string status, bool writeToChat)
        {
            if (model != null)
            {
                model.CompositionStatus = status;
            }
            compositionExportProgress?.UpdateStatus(status);
            if (writeToChat && ui?.ChatBox != null)
            {
                ui.ChatBox.AppendTextHelp(status);
            }
        }

        private void LoadCompositionOptionData(UIOptionsDataModel model)
        {
            var config = MapRenderConfig.Default;
            model.CompositionDurationSeconds = ((double)config.CompositionDurationSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            model.CompositionFrameRate = ((double)config.CompositionFrameRate).ToString("0.###", CultureInfo.InvariantCulture);
            model.CompositionViewportWidth = ((int)config.CompositionViewportWidth).ToString(CultureInfo.InvariantCulture);
            model.CompositionViewportHeight = ((int)config.CompositionViewportHeight).ToString(CultureInfo.InvariantCulture);

            var rect = !CaptureRect.IsEmpty
                ? CaptureRect
                : mapData?.VRect ?? renderEnv?.Camera?.WorldRect ?? Rectangle.Empty;
            model.CompositionWorldLeft = rect.Left.ToString(CultureInfo.InvariantCulture);
            model.CompositionWorldTop = rect.Top.ToString(CultureInfo.InvariantCulture);
            model.CompositionWorldRight = rect.Right.ToString(CultureInfo.InvariantCulture);
            model.CompositionWorldBottom = rect.Bottom.ToString(CultureInfo.InvariantCulture);

            var outputDirectory = (string)config.CompositionOutputDirectory;
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            model.CompositionAepPath = Path.Combine(outputDirectory, GetDefaultCompositionFileName());
            model.CompositionStatus = compositionExportRunning ? "AEP를 생성하고 있습니다..." : string.Empty;
        }

        private void SaveCompositionOptionData(UIOptionsDataModel model, MapRenderConfig config)
        {
            if (model == null || config == null)
            {
                return;
            }

            if (TryParsePositiveDouble(model.CompositionDurationSeconds, out var duration))
            {
                config.CompositionDurationSeconds = duration;
            }
            if (int.TryParse(model.CompositionFrameRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameRate)
                && frameRate >= 1 && frameRate <= 120)
            {
                config.CompositionFrameRate = frameRate;
            }
            if (int.TryParse(model.CompositionViewportWidth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) && width > 0)
            {
                config.CompositionViewportWidth = width;
            }
            if (int.TryParse(model.CompositionViewportHeight, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) && height > 0)
            {
                config.CompositionViewportHeight = height;
            }

            try
            {
                var fullPath = Path.GetFullPath(model.CompositionAepPath ?? string.Empty);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    config.CompositionOutputDirectory = directory;
                }
            }
            catch (Exception)
            {
            }
        }

        private void SaveCompositionSettings(UIOptionsDataModel model, string creatorPath, string afterFxPath)
        {
            WzComparerR2.Config.ConfigManager.Reload();
            var config = MapRenderConfig.Default;
            SaveCompositionOptionData(model, config);
            config.AfterEffectTemplateCreatorPath = creatorPath;
            config.AfterFxPath = afterFxPath;
            WzComparerR2.Config.ConfigManager.Save();
        }

        private bool TryBuildCompositionRequest(
            UIOptionsDataModel model,
            out MapCompositionRequest request,
            out string error)
        {
            request = null;
            error = null;

            string outputPath;
            try
            {
                outputPath = Path.GetFullPath(model.CompositionAepPath ?? string.Empty);
            }
            catch (Exception ex)
            {
                error = "AEP 경로가 올바르지 않습니다: " + ex.Message;
                return false;
            }
            if (!string.Equals(Path.GetExtension(outputPath), ".aep", StringComparison.OrdinalIgnoreCase))
            {
                error = "AEP 경로는 .aep 확장자여야 합니다.";
                return false;
            }
            if (outputPath.IndexOf('"') >= 0)
            {
                error = "AEP 경로에는 큰따옴표를 사용할 수 없습니다.";
                return false;
            }

            if (!TryParsePositiveDouble(model.CompositionDurationSeconds, out var duration) ||
                duration > AfterEffectsMaximumDurationSeconds)
            {
                error = "길이는 0초보다 크고 After Effects 제한인 3시간 이하여야 합니다.";
                return false;
            }
            if (!int.TryParse(model.CompositionFrameRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameRate) ||
                frameRate < 1 || frameRate > 120)
            {
                error = "FPS는 1~120 사이의 정수여야 합니다.";
                return false;
            }
            if (!TryParsePositiveDimension(model.CompositionViewportWidth, out var viewportWidth) ||
                !TryParsePositiveDimension(model.CompositionViewportHeight, out var viewportHeight))
            {
                error = $"뷰포트 크기는 1~{AfterEffectsMaximumCompositionDimension:N0} 픽셀이어야 합니다.";
                return false;
            }
            if (!TryParseInt(model.CompositionWorldLeft, out var left) ||
                !TryParseInt(model.CompositionWorldTop, out var top) ||
                !TryParseInt(model.CompositionWorldRight, out var right) ||
                !TryParseInt(model.CompositionWorldBottom, out var bottom) ||
                right <= left || bottom <= top)
            {
                error = "월드 범위의 좌/상/우/하 값을 확인해 주세요.";
                return false;
            }

            var width = (long)right - left;
            var height = (long)bottom - top;
            if (width > AfterEffectsMaximumCompositionDimension || height > AfterEffectsMaximumCompositionDimension)
            {
                error = $"월드 컴프는 After Effects의 {AfterEffectsMaximumCompositionDimension:N0}×{AfterEffectsMaximumCompositionDimension:N0} 제한을 넘을 수 없습니다. CaptureRect를 줄여 주세요.";
                return false;
            }

            request = new MapCompositionRequest
            {
                OutputAepPath = outputPath,
                DurationSeconds = duration,
                FrameRate = frameRate,
                ViewportWidth = viewportWidth,
                ViewportHeight = viewportHeight,
                WorldRect = new Rectangle(left, top, (int)width, (int)height),
                CompositionPrefix = GetDefaultCompositionPrefix()
            };
            return true;
        }

        private string GetDefaultCompositionFileName()
        {
            return GetDefaultCompositionPrefix() + ".aep";
        }

        private string GetDefaultCompositionPrefix()
        {
            var id = (mapData?.ID ?? 0).ToString("D9", CultureInfo.InvariantCulture);
            var name = MakeSafeName(GetMapDisplayName());
            return string.IsNullOrWhiteSpace(name) ? id : id + "_" + name;
        }

        private string GetMapDisplayName()
        {
            if (mapData?.ID != null && StringLinker?.StringMap != null &&
                StringLinker.StringMap.TryGetValue(mapData.ID.Value, out var stringResult))
            {
                var mapName = stringResult?["mapName"];
                if (!string.IsNullOrWhiteSpace(mapName))
                {
                    return mapName;
                }
            }
            return mapData?.Name ?? "Map";
        }

        private static string MakeSafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var builder = new StringBuilder(value.Length);
            foreach (var character in value.Trim())
            {
                builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);
            }
            var result = builder.ToString().Trim(' ', '.');
            return result.Length > 100 ? result.Substring(0, 100) : result;
        }

        private string LocateAfterEffectTemplateCreator()
        {
            var configured = (string)MapRenderConfig.Default.AfterEffectTemplateCreatorPath;
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string>
            {
                Path.Combine(baseDirectory, "AfterEffectTemplateCreator.exe"),
                Path.Combine(baseDirectory, "AfterEffectTemplateCreator", "AfterEffectTemplateCreator.exe")
            };
            foreach (var ancestor in EnumerateAncestors(baseDirectory, 7))
            {
                var sibling = Path.Combine(ancestor, "AfterEffectTemplateCreator");
                candidates.Add(Path.Combine(sibling, "AfterEffectTemplateCreator.exe"));
                candidates.Add(Path.Combine(sibling, "publish", "win-x64-map-composition", "AfterEffectTemplateCreator.exe"));
                candidates.Add(Path.Combine(sibling, "src", "AfterEffectTemplateCreator", "bin", "Release", "net10.0-windows", "win-x64", "publish", "AfterEffectTemplateCreator.exe"));
                candidates.Add(Path.Combine(sibling, "src", "AfterEffectTemplateCreator", "bin", "Release", "net10.0-windows", "win-x64", "AfterEffectTemplateCreator.exe"));
                candidates.Add(Path.Combine(sibling, "src", "AfterEffectTemplateCreator", "bin", "Release", "net10.0-windows", "AfterEffectTemplateCreator.exe"));
            }
            return candidates
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private string LocateAfterFx()
        {
            var configured = (string)MapRenderConfig.Default.AfterFxPath;
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }

            var candidates = new List<string>();
            var environmentPath = Environment.GetEnvironmentVariable("AFTERFX_PATH");
            if (!string.IsNullOrWhiteSpace(environmentPath))
            {
                candidates.Add(environmentPath);
            }
            candidates.Add(@"F:\Adobe\Adobe After Effects 2026\Support Files\AfterFX.com");
            candidates.Add(@"C:\Program Files\Adobe\Adobe After Effects 2026\Support Files\AfterFX.com");
            AddAfterFxCandidates(candidates, @"F:\Adobe");
            AddAfterFxCandidates(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Adobe"));
            AddAfterFxCandidates(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Adobe"));
            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static void AddAfterFxCandidates(ICollection<string> candidates, string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return;
            }
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "Adobe After Effects*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(Path.Combine(directory, "Support Files", "AfterFX.com"));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static IEnumerable<string> EnumerateAncestors(string path, int maximumCount)
        {
            var directory = new DirectoryInfo(path);
            for (var i = 0; directory != null && i < maximumCount; i++, directory = directory.Parent)
            {
                yield return directory.FullName;
            }
        }

        private static string PromptForExecutable(string title, string filter)
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Title = title;
                dialog.Filter = filter;
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                dialog.RestoreDirectory = true;
                return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? Path.GetFullPath(dialog.FileName)
                    : null;
            }
        }

        private static bool TryParsePositiveDouble(string value, out double parsed)
        {
            return (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                    double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)) &&
                   !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed > 0;
        }

        private static bool TryParseInt(string value, out int parsed)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ||
                   int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out parsed);
        }

        private static bool TryParsePositiveDimension(string value, out int parsed)
        {
            return TryParseInt(value, out parsed) && parsed > 0 && parsed <= AfterEffectsMaximumCompositionDimension;
        }

        private static string QuoteCommandLineArgument(string value)
        {
            if (value.IndexOf('"') >= 0)
            {
                throw new ArgumentException("Command-line path cannot contain a double quote.", nameof(value));
            }
            return '"' + value + '"';
        }

        private static string FormatProcessOutput(StringBuilder output, StringBuilder error)
        {
            var text = new StringBuilder();
            lock (error)
            {
                if (error.Length > 0) text.AppendLine().Append(error.ToString().Trim());
            }
            lock (output)
            {
                if (output.Length > 0) text.AppendLine().Append(output.ToString().Trim());
            }
            return text.Length == 0 ? string.Empty : Environment.NewLine + text.ToString();
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        private static bool TryWriteCancellationRequest(string cancellationPath)
        {
            try
            {
                File.WriteAllText(cancellationPath, "cancel" + Environment.NewLine, new UTF8Encoding(false));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void TryKillProcessTree(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (process.HasExited)
                {
                    return;
                }

                using (var taskKill = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    taskKill?.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            TryKill(process);
        }

        private static void WriteJson<T>(string path, T value)
        {
            var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
        }

        private static T ReadJson<T>(string path)
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false
            });
        }

        private sealed class MapCompositionRequest
        {
            public string OutputAepPath { get; set; }
            public string CompositionPrefix { get; set; }
            public int FrameRate { get; set; }
            public double DurationSeconds { get; set; }
            public int ViewportWidth { get; set; }
            public int ViewportHeight { get; set; }
            public Rectangle WorldRect { get; set; }
        }

        private sealed class MapCompositionRunContext
        {
            public MapCompositionRunContext(
                MapCompositionTransaction transaction,
                MapCompositionExportResult exportResult,
                string jobPath,
                string resultPath,
                string logPath,
                string cancellationPath)
            {
                Transaction = transaction;
                ExportResult = exportResult;
                JobPath = jobPath;
                ResultPath = resultPath;
                LogPath = logPath;
                CancellationPath = cancellationPath;
            }

            public MapCompositionTransaction Transaction { get; }
            public MapCompositionExportResult ExportResult { get; }
            public string JobPath { get; }
            public string ResultPath { get; }
            public string LogPath { get; }
            public string CancellationPath { get; }
        }

        private sealed class MapCompositionProcessResult
        {
            public MapCompositionProcessResult(string outputAepPath, string logPath, string[] warnings)
            {
                OutputAepPath = outputAepPath;
                LogPath = logPath;
                Warnings = warnings;
            }

            public string OutputAepPath { get; }
            public string LogPath { get; }
            public string[] Warnings { get; }
        }

        private sealed class MapCompositionJobDocument
        {
            public string Schema { get; set; }
            public int Version { get; set; }
            public string ManifestPath { get; set; }
            public string TemporaryAepPath { get; set; }
            public string OutputAepPath { get; set; }
            public string AfterFxPath { get; set; }
            public string CompPrefix { get; set; }
            public string LogPath { get; set; }
            public string ResultPath { get; set; }
            public string CancellationPath { get; set; }
        }

        private sealed class MapCompositionResultDocument
        {
            public string Schema { get; set; }
            public int Version { get; set; }
            public bool Success { get; set; }
            public string OutputAepPath { get; set; }
            public string LogPath { get; set; }
            public string[] Warnings { get; set; }
            public string Error { get; set; }
        }

        private sealed class MapCompositionTransaction
        {
            private readonly string token = Guid.NewGuid().ToString("N");
            private string backupAepPath;
            private string backupMapPath;
            private bool began;
            private bool prepared;
            private bool completed;
            private bool aepBackedUp;
            private bool mapBackedUp;
            private bool assetsDirectoryCreated;

            public MapCompositionTransaction(string outputAepPath)
            {
                OutputAepPath = Path.GetFullPath(outputAepPath);
                var outputDirectory = Path.GetDirectoryName(OutputAepPath)
                    ?? throw new InvalidOperationException("AEP output directory is missing.");
                var baseName = Path.GetFileNameWithoutExtension(OutputAepPath);
                AssetsDirectory = Path.Combine(outputDirectory, baseName + ".assets");
                MapDirectory = Path.Combine(AssetsDirectory, "map");
                TemporaryAepPath = Path.Combine(outputDirectory, "." + baseName + ".wzmap." + token + ".aep");
                backupAepPath = Path.Combine(outputDirectory, "." + baseName + ".wzbackup." + token + ".aep");
                backupMapPath = Path.Combine(AssetsDirectory, "map.wzbackup." + token);
            }

            public string OutputAepPath { get; }
            public string TemporaryAepPath { get; }
            public string AssetsDirectory { get; }
            public string MapDirectory { get; }

            public void Begin()
            {
                if (began) throw new InvalidOperationException("The map composition transaction already began.");
                Directory.CreateDirectory(Path.GetDirectoryName(OutputAepPath));
                assetsDirectoryCreated = !Directory.Exists(AssetsDirectory);
                Directory.CreateDirectory(AssetsDirectory);
                began = true;
                try
                {
                    if (File.Exists(OutputAepPath))
                    {
                        File.Move(OutputAepPath, backupAepPath);
                        aepBackedUp = true;
                    }
                    if (Directory.Exists(MapDirectory))
                    {
                        Directory.Move(MapDirectory, backupMapPath);
                        mapBackedUp = true;
                    }
                    TryDeleteFile(TemporaryAepPath);
                    prepared = true;
                }
                catch (Exception beginException)
                {
                    try
                    {
                        Rollback();
                    }
                    catch (Exception rollbackException)
                    {
                        throw new AggregateException(
                            "The map composition transaction could not begin or restore its backups.",
                            beginException,
                            rollbackException);
                    }
                    throw;
                }
            }

            public void Commit()
            {
                if (!began || completed) return;
                completed = true;
                TryIgnore(() => TryDeleteFile(backupAepPath));
                TryIgnore(() => TryDeleteDirectory(backupMapPath));
                TryIgnore(() => TryDeleteFile(TemporaryAepPath));
            }

            public void Rollback()
            {
                if (!began || completed) return;
                var errors = new List<Exception>();
                if (prepared)
                {
                    TryAction(() => TryDeleteFile(OutputAepPath), errors);
                    TryAction(() => TryDeleteFile(TemporaryAepPath), errors);
                    TryAction(() => TryDeleteDirectory(MapDirectory), errors);
                }
                TryAction(() =>
                {
                    if (aepBackedUp && File.Exists(backupAepPath)) File.Move(backupAepPath, OutputAepPath);
                }, errors);
                TryAction(() =>
                {
                    if (mapBackedUp && Directory.Exists(backupMapPath)) Directory.Move(backupMapPath, MapDirectory);
                }, errors);
                if (assetsDirectoryCreated)
                {
                    TryAction(() =>
                    {
                        if (Directory.Exists(AssetsDirectory) && !Directory.EnumerateFileSystemEntries(AssetsDirectory).Any())
                        {
                            Directory.Delete(AssetsDirectory, false);
                        }
                    }, errors);
                }
                completed = errors.Count == 0;
                if (errors.Count > 0) throw new AggregateException(errors);
            }

            private static void TryIgnore(Action action)
            {
                try { action(); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            private static void TryAction(Action action, ICollection<Exception> errors)
            {
                try { action(); }
                catch (Exception ex) { errors.Add(ex); }
            }

            private static void TryDeleteFile(string path)
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
            }

            private static void TryDeleteDirectory(string path)
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Directory.Delete(path, true);
            }
        }
    }
}
