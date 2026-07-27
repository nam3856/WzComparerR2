using System;
using System.Threading;
using System.Windows.Forms;

namespace WzComparerR2.MapRender.UI
{
    /// <summary>
    /// Runs on its own message-loop thread so cancellation remains available while
    /// the MapRender thread is synchronously baking GPU-backed Spine frames.
    /// </summary>
    internal sealed class CompositionExportProgressForm : IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly Action cancelAction;
        private Thread uiThread;
        private Form form;
        private Label statusLabel;
        private Button cancelButton;
        private string pendingStatus;
        private bool closeRequested;
        private bool disposed;

        public CompositionExportProgressForm(Action cancelAction)
        {
            this.cancelAction = cancelAction ?? throw new ArgumentNullException(nameof(cancelAction));
        }

        public void Show(string status)
        {
            lock (syncRoot)
            {
                if (disposed || uiThread != null)
                {
                    return;
                }

                pendingStatus = status ?? string.Empty;
                uiThread = new Thread(RunMessageLoop)
                {
                    IsBackground = true,
                    Name = "MapRender AEP export progress"
                };
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.Start();
            }
        }

        public void UpdateStatus(string status)
        {
            Form currentForm;
            lock (syncRoot)
            {
                pendingStatus = status ?? string.Empty;
                currentForm = form;
            }

            InvokeIfAvailable(currentForm, ApplyPendingStatus);
        }

        public void Dispose()
        {
            Form currentForm;
            lock (syncRoot)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                closeRequested = true;
                currentForm = form;
            }

            InvokeIfAvailable(currentForm, () => currentForm.Close());
        }

        private void RunMessageLoop()
        {
            var progressForm = new Form
            {
                Text = "AEP 만들기",
                Width = 430,
                Height = 150,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowIcon = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog
            };

            var label = new Label
            {
                AutoEllipsis = true,
                Left = 18,
                Top = 18,
                Width = 378,
                Height = 42,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            var button = new Button
            {
                Text = "생성 취소",
                Width = 92,
                Height = 28,
                Left = 304,
                Top = 70
            };
            button.Click += (sender, args) => RequestCancellation();
            progressForm.Controls.Add(label);
            progressForm.Controls.Add(button);
            progressForm.CancelButton = button;
            progressForm.FormClosing += (sender, args) =>
            {
                lock (syncRoot)
                {
                    if (!closeRequested)
                    {
                        args.Cancel = true;
                    }
                }

                if (args.Cancel)
                {
                    RequestCancellation();
                }
            };

            lock (syncRoot)
            {
                form = progressForm;
                statusLabel = label;
                cancelButton = button;
            }
            ApplyPendingStatus();

            bool closeImmediately;
            lock (syncRoot)
            {
                closeImmediately = closeRequested;
            }
            if (closeImmediately)
            {
                progressForm.Dispose();
                ClearControls(progressForm);
                return;
            }

            try
            {
                Application.Run(progressForm);
            }
            finally
            {
                ClearControls(progressForm);
                progressForm.Dispose();
            }
        }

        private void RequestCancellation()
        {
            Button currentButton;
            Label currentLabel;
            lock (syncRoot)
            {
                currentButton = cancelButton;
                currentLabel = statusLabel;
            }

            if (currentButton != null)
            {
                currentButton.Enabled = false;
            }
            if (currentLabel != null)
            {
                currentLabel.Text = "취소 요청을 처리하고 있습니다...";
            }
            cancelAction();
        }

        private void ApplyPendingStatus()
        {
            Label currentLabel;
            string status;
            lock (syncRoot)
            {
                currentLabel = statusLabel;
                status = pendingStatus;
            }
            if (currentLabel != null)
            {
                currentLabel.Text = status;
            }
        }

        private void ClearControls(Form expectedForm)
        {
            lock (syncRoot)
            {
                if (ReferenceEquals(form, expectedForm))
                {
                    form = null;
                    statusLabel = null;
                    cancelButton = null;
                }
            }
        }

        private static void InvokeIfAvailable(Form target, Action action)
        {
            if (target == null || target.IsDisposed || !target.IsHandleCreated)
            {
                return;
            }

            try
            {
                target.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
