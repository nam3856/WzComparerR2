using System;
using System.IO;
using System.Threading;
using WzComparerR2.Config;
using WzComparerR2.MapRender.Config;
using WzComparerR2.MapRender.Export;
using WzComparerR2.MapRender.UI;
using Forms = System.Windows.Forms;

namespace WzComparerR2.MapRender
{
    public partial class FrmMapRender2
    {
        private bool unityMapExportRunning;

        private void BeginUnityMapExport()
        {
            if (unityMapExportRunning) return;
            if (mapData == null)
            {
                ui.ChatBox.AppendTextWarning("먼저 맵을 불러와 주세요.");
                return;
            }
            try { ShowUnityMapExportDialog(); }
            finally
            {
                // The dialog and GPU export pause this thread. Resume the preview
                // from its existing animation time instead of catching up that pause.
                ResetElapsedTime();
            }
        }

        private void ShowUnityMapExportDialog()
        {
            using (var picker = new Forms.FolderBrowserDialog
            {
                Description = "Unity 내보내기 폴더 (선택한 폴더 아래 Maps/맵ID에 저장)",
                SelectedPath = MapRenderConfig.Default.UnityOutputDirectory?.Value ?? string.Empty,
                ShowNewFolderButton = true
            })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                string destination = Path.Combine(picker.SelectedPath, "Maps", (mapData.ID ?? 0).ToString("D9"));
                ConfigManager.Reload();
                MapRenderConfig.Default.UnityOutputDirectory = picker.SelectedPath;
                ConfigManager.Save();
                unityMapExportRunning = true;
                using (var cancellation = new CancellationTokenSource())
                using (var progress = new CompositionExportProgressForm(cancellation.Cancel))
                {
                    progress.Show("맵 자산과 원본 배치를 추출하고 있습니다...");
                    try
                    {
                        MapRenderSpineSequenceBaker.TryCreate(GraphicsDevice, Services, out var baker);
                        var exporter = new MapUnityExporter { GraphicsDevice = GraphicsDevice };
                        var result = exporter.Export(mapData, destination,
                            new MapCompositionExportOptions { FrameRate = 30, DisplayMode = renderEnv.Camera.DisplayMode, ViewportWidth = renderEnv.Camera.Width,
                                ViewportHeight = renderEnv.Camera.Height, Visibility = patchVisibility, SpineBaker = baker },
                            cancellation.Token, new UnityMapProgress(p => progress.UpdateStatus(p.Phase + " " + p.Completed + "/" + p.Total)));
                        string message = (result.warnings.Count == 0 ? "Unity 맵 내보내기 완료" : "Unity 맵 내보내기 완료 (경고 " + result.warnings.Count + "개)")
                            + "\r\n" + destination + "\r\nMapleLike의 Tools > WZ Importer > Import Export Folder...에서 이 폴더를 선택하세요.";
                        ui.ChatBox.AppendTextSystem(message);
                        progress.Dispose();
                        Forms.MessageBox.Show(message, "Unity 맵 내보내기", Forms.MessageBoxButtons.OK,
                            result.warnings.Count == 0 ? Forms.MessageBoxIcon.Information : Forms.MessageBoxIcon.Warning);
                    }
                    catch (OperationCanceledException)
                    {
                        ui.ChatBox.AppendTextSystem("Unity 맵 내보내기를 취소했습니다. 기존 내보내기는 보존됩니다.");
                    }
                    catch (Exception error)
                    {
                        progress.Dispose();
                        ui.ChatBox.AppendTextWarning("Unity 맵 내보내기 실패: " + error.Message);
                        Forms.MessageBox.Show(error.ToString(), "Unity 맵 내보내기 실패", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
                    }
                    finally { unityMapExportRunning = false; }
                }
            }
        }

        private sealed class UnityMapProgress : IProgress<MapCompositionExportProgress>
        {
            private readonly Action<MapCompositionExportProgress> callback;
            public UnityMapProgress(Action<MapCompositionExportProgress> callback) { this.callback = callback; }
            public void Report(MapCompositionExportProgress value) { callback(value); }
        }
    }
}
