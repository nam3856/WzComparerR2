using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WzComparerR2.UnityExport
{
    public sealed class UnityExportDialog : Form
    {
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Action<CancellationToken> export;
        private readonly Label message;
        private bool finished;
        private Exception error;

        private UnityExportDialog(Action<CancellationToken> export)
        {
            this.export = export;
            Text = "Unity 추출";
            Width = 460; Height = 150;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            message = new Label { Left = 15, Top = 18, Width = 420, Height = 35, Text = "PNG와 애니메이션 데이터를 추출하고 있습니다..." };
            var cancel = new Button { Left = 340, Top = 65, Width = 85, Text = "취소" };
            cancel.Click += (s, e) => { cancellation.Cancel(); message.Text = "취소하는 중입니다. 기존 결과는 보존됩니다."; cancel.Enabled = false; };
            Controls.Add(message); Controls.Add(cancel);
            Shown += Run;
            FormClosing += (s, e) => { if (!finished) { cancellation.Cancel(); e.Cancel = true; } };
        }

        private async void Run(object sender, EventArgs e)
        {
            try { await Task.Run(() => export(cancellation.Token)); }
            catch (Exception ex) { error = ex; }
            finally { finished = true; DialogResult = error == null ? DialogResult.OK : DialogResult.Cancel; Close(); }
        }

        public static bool Run(IWin32Window owner, Action<CancellationToken> export)
        {
            using (var dialog = new UnityExportDialog(export))
            {
                dialog.ShowDialog(owner);
                if (dialog.error != null && !(dialog.error is OperationCanceledException)) throw dialog.error;
                return dialog.error == null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) cancellation.Dispose();
            base.Dispose(disposing);
        }
    }
}
