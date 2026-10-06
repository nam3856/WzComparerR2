using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevComponents.DotNetBar;
using WzComparerR2.Avatar.Export;
using WzComparerR2.Controls;
using WzUnity;

namespace WzComparerR2.Avatar.UI
{
    internal partial class AvatarForm
    {
        private void ConfigureUnityExport()
        {
            var button = new ButtonItem("btnExportUnity", "캐릭터 Unity 추출")
            {
                Image = Properties.Resources.export,
                ButtonStyle = eButtonStyle.ImageAndText,
                Tooltip = "현재 캐릭터를 부위별 애니메이션으로 내보내기"
            };
            button.Click += ExportUnityAvatar_Click;
            bar3.Items.Add(button);
        }

        private void ExportUnityAvatar_Click(object sender, EventArgs e)
        {
            if (avatar.Body == null || avatar.Head == null)
            {
                MessageBoxEx.Show("캐릭터를 먼저 구성하세요.", "Unity 내보내기");
                return;
            }
            DialogResult scope = MessageBoxEx.Show(
                "현재 장착 부위와 선택한 표정, 장비 이펙트를 내보냅니다.\r\n\r\n주요 동작만 내보내겠습니까?\r\n예: 주요 동작\r\n아니요: 전체 동작",
                "Unity 캐릭터 내보내기", MessageBoxButtons.YesNoCancel);
            if (scope == DialogResult.Cancel) return;
            string[] actions = avatar.Actions.Where(action => scope != DialogResult.Yes || action.Level == 0)
                .Select(action => action.Name).ToArray();
            using (var dialog = new FolderBrowserDialog { Description = "캐릭터별 하위 폴더를 만들 위치를 선택하세요." })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var exporter = new UnityAvatarExporter(avatar);
                string outputPath = Path.Combine(dialog.SelectedPath, exporter.AppearanceId);
                WzUnityManifest result = null;
                Task<WzUnityManifest> exportTask = null;
                bool acceptProgress = true;
                bool wasEnabled = this.Enabled;
                bool wasLocked = btnLock.Checked;
                bool wasTimerEnabled = timer1.Enabled;
                try
                {
                    this.Enabled = false;
                    btnLock.Checked = true;
                    timer1.Stop();
                    ProgressDialog.Show(this.FindForm(), "Unity 캐릭터 내보내기", "부위를 준비하는 중...", true, false,
                        async (context, cancellationToken) =>
                        {
                            context.ProgressMin = 0;
                            context.ProgressMax = Math.Max(1, actions.Length);
                            var progress = new Progress<Tuple<int, int, string>>(value =>
                            {
                                if (!acceptProgress) return;
                                context.Progress = value.Item1;
                                context.Message = value.Item3;
                            });
                            try
                            {
                                exportTask = Task.Run(() => exporter.Export(outputPath, actions,
                                    cancellationToken, (current, total, message) =>
                                        ((IProgress<Tuple<int, int, string>>)progress).Report(Tuple.Create(current, total, message))));
                                result = await exportTask;
                            }
                            catch (Exception ex)
                            {
                                if (acceptProgress)
                                {
                                    context.Message = ex is OperationCanceledException ? "내보내기를 취소했습니다."
                                        : "오류: " + ex.Message;
                                    context.FullMessage = ex.ToString();
                                }
                                acceptProgress = false;
                                throw;
                            }
                        });
                    acceptProgress = false;
                    // The shared progress dialog closes immediately on cancellation.
                    // Await the worker before allowing equipment or WZ data to change.
                    if (exportTask != null) result = exportTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    MessageBoxEx.Show("캐릭터를 내보내지 못했습니다.\r\n" + ex.Message, "Unity 내보내기 오류");
                    return;
                }
                finally
                {
                    acceptProgress = false;
                    this.Enabled = wasEnabled;
                    btnLock.Checked = wasLocked;
                    timer1.Enabled = wasTimerEnabled;
                }
                if (result != null)
                {
                    MessageBoxEx.Show("Unity 캐릭터 데이터를 저장했습니다.\r\n동작: " + result.entities[0].clips.Count
                        + ", 이미지: " + result.assets.Count + ", 경고: " + result.warnings.Count
                        + "\r\n" + outputPath
                        + "\r\nUnity에서 WZ Importer로 이 폴더를 가져오세요.", "Unity 내보내기 완료");
                }
            }
        }
    }
}
