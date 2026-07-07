using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace WzComparerR2.ImageSearch
{
    internal sealed class FrmImageSearch : Form
    {
        private readonly PluginContext context;
        private readonly MainFormNavigator navigator;
        private ImageBuffer referenceImage;
        private CancellationTokenSource cancellationTokenSource;

        private PictureBox pictureReference;
        private Label labelReference;
        private ComboBox comboScope;
        private NumericUpDown numericThreshold;
        private NumericUpDown numericSizeTolerance;
        private CheckBox checkIgnoreSize;
        private CheckBox checkIncludeSpine;
        private NumericUpDown numericSpineSamples;
        private NumericUpDown numericMaxResults;
        private Button buttonFile;
        private Button buttonClipboard;
        private Button buttonSearch;
        private Button buttonCancel;
        private ProgressBar progressBar;
        private Label labelStatus;
        private ListView listResults;

        public FrmImageSearch(PluginContext context)
        {
            this.context = context;
            navigator = new MainFormNavigator(context);
            InitializeComponent();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cancellationTokenSource?.Cancel();
            base.OnFormClosing(e);
        }

        private void InitializeComponent()
        {
            Text = "이미지 검색";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 520);
            Size = new Size(900, 620);

            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            FlowLayoutPanel topBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true
            };
            root.Controls.Add(topBar, 0, 0);

            buttonFile = new Button { Text = "파일", Width = 88, Height = 28 };
            buttonFile.Click += ButtonFile_Click;
            topBar.Controls.Add(buttonFile);

            buttonClipboard = new Button { Text = "클립보드", Width = 88, Height = 28 };
            buttonClipboard.Click += ButtonClipboard_Click;
            topBar.Controls.Add(buttonClipboard);

            topBar.Controls.Add(CreateLabel("범위"));
            comboScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
            comboScope.Items.AddRange(new object[] { "열린 WZ 전체", "선택 노드 하위" });
            comboScope.SelectedIndex = 0;
            topBar.Controls.Add(comboScope);

            topBar.Controls.Add(CreateLabel("점수"));
            numericThreshold = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 100,
                DecimalPlaces = 1,
                Increment = 1,
                Value = 90,
                Width = 64
            };
            topBar.Controls.Add(numericThreshold);

            topBar.Controls.Add(CreateLabel("크기허용"));
            numericSizeTolerance = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 32,
                Value = 0,
                Width = 56
            };
            topBar.Controls.Add(numericSizeTolerance);

            checkIgnoreSize = new CheckBox
            {
                Text = "크기 무시",
                AutoSize = true,
                Height = 28,
                Margin = new Padding(8, 6, 0, 0)
            };
            checkIgnoreSize.CheckedChanged += CheckIgnoreSize_CheckedChanged;
            topBar.Controls.Add(checkIgnoreSize);

            checkIncludeSpine = new CheckBox
            {
                Text = "Spine 포함",
                AutoSize = true,
                Height = 28,
                Margin = new Padding(8, 6, 0, 0)
            };
            checkIncludeSpine.CheckedChanged += CheckIncludeSpine_CheckedChanged;
            topBar.Controls.Add(checkIncludeSpine);

            topBar.Controls.Add(CreateLabel("Spine샘플"));
            numericSpineSamples = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 8,
                Value = 3,
                Width = 48,
                Enabled = false
            };
            topBar.Controls.Add(numericSpineSamples);

            topBar.Controls.Add(CreateLabel("최대"));
            numericMaxResults = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1000,
                Value = 200,
                Width = 64
            };
            topBar.Controls.Add(numericMaxResults);

            buttonSearch = new Button { Text = "검색", Width = 88, Height = 28 };
            buttonSearch.Click += ButtonSearch_Click;
            topBar.Controls.Add(buttonSearch);

            buttonCancel = new Button { Text = "취소", Width = 88, Height = 28, Enabled = false };
            buttonCancel.Click += ButtonCancel_Click;
            topBar.Controls.Add(buttonCancel);

            TableLayoutPanel previewPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(0, 8, 0, 8)
            };
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(previewPanel, 0, 1);

            pictureReference = new PictureBox
            {
                Width = 112,
                Height = 72,
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.White
            };
            previewPanel.Controls.Add(pictureReference, 0, 0);

            labelReference = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "기준 이미지 없음"
            };
            previewPanel.Controls.Add(labelReference, 1, 0);

            listResults = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false
            };
            listResults.Columns.Add("Score", 80);
            listResults.Columns.Add("Type", 150);
            listResults.Columns.Add("Size", 90);
            listResults.Columns.Add("Path", 560);
            listResults.MouseDoubleClick += ListResults_MouseDoubleClick;
            root.Controls.Add(listResults, 0, 2);

            TableLayoutPanel statusPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2
            };
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            root.Controls.Add(statusPanel, 0, 3);

            labelStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = "대기"
            };
            statusPanel.Controls.Add(labelStatus, 0, 0);

            progressBar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Style = ProgressBarStyle.Blocks
            };
            statusPanel.Controls.Add(progressBar, 1, 0);
        }

        private static Label CreateLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(12, 7, 2, 0)
            };
        }

        private void ButtonFile_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files|*.*";
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                using (Image image = Image.FromFile(dialog.FileName))
                using (Bitmap bitmap = new Bitmap(image))
                {
                    SetReferenceImage(bitmap, Path.GetFileName(dialog.FileName));
                }
            }
        }

        private void ButtonClipboard_Click(object sender, EventArgs e)
        {
            if (!Clipboard.ContainsImage())
            {
                MessageBox.Show(this, "클립보드에 이미지가 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (Image image = Clipboard.GetImage())
            {
                if (image == null)
                {
                    return;
                }

                using (Bitmap bitmap = new Bitmap(image))
                {
                    SetReferenceImage(bitmap, "Clipboard");
                }
            }
        }

        private async void ButtonSearch_Click(object sender, EventArgs e)
        {
            if (referenceImage == null)
            {
                MessageBox.Show(this, "기준 이미지를 먼저 선택하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<Wz_Node> roots = GetSearchRoots();
            if (roots.Count == 0)
            {
                MessageBox.Show(this, "검색할 WZ 노드가 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            listResults.Items.Clear();
            cancellationTokenSource = new CancellationTokenSource();
            SetSearching(true);

            double threshold = (double)numericThreshold.Value;
            int sizeTolerance = (int)numericSizeTolerance.Value;
            bool ignoreSize = checkIgnoreSize.Checked;
            bool includeSpine = checkIncludeSpine.Checked;
            int spineSamples = (int)numericSpineSamples.Value;
            int maxResults = (int)numericMaxResults.Value;
            IProgress<SearchProgress> progress = new Progress<SearchProgress>(UpdateProgress);
            SpineFrameRenderer spineRenderer = includeSpine ? new SpineFrameRenderer(context) : null;

            try
            {
                List<SearchResult> results = await Task.Run(() => ImageSearchEngine.Search(
                    roots,
                    referenceImage,
                    threshold,
                    sizeTolerance,
                    ignoreSize,
                    includeSpine,
                    spineSamples,
                    spineRenderer,
                    maxResults,
                    progress,
                    cancellationTokenSource.Token));

                ShowResults(results);
                labelStatus.Text = $"완료: {results.Count}개 결과";
            }
            catch (OperationCanceledException)
            {
                labelStatus.Text = "취소됨";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                labelStatus.Text = "오류";
            }
            finally
            {
                SetSearching(false);
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }
        }

        private void ButtonCancel_Click(object sender, EventArgs e)
        {
            cancellationTokenSource?.Cancel();
        }

        private void CheckIgnoreSize_CheckedChanged(object sender, EventArgs e)
        {
            numericSizeTolerance.Enabled = !checkIgnoreSize.Checked && !buttonCancel.Enabled;
        }

        private void CheckIncludeSpine_CheckedChanged(object sender, EventArgs e)
        {
            numericSpineSamples.Enabled = checkIncludeSpine.Checked && !buttonCancel.Enabled;
        }

        private void ListResults_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (listResults.SelectedItems.Count == 0)
            {
                return;
            }

            SearchResult result = listResults.SelectedItems[0].Tag as SearchResult;
            if (navigator.TrySelect(result, out string error))
            {
                labelStatus.Text = "선택됨: " + result.FullPath;
            }
            else
            {
                labelStatus.Text = error;
            }
        }

        private void SetReferenceImage(Bitmap bitmap, string sourceName)
        {
            referenceImage = ImageBuffer.FromBitmap(bitmap);

            Image oldImage = pictureReference.Image;
            pictureReference.Image = new Bitmap(bitmap);
            oldImage?.Dispose();

            labelReference.Text = $"{sourceName} ({referenceImage.Width} x {referenceImage.Height})";
        }

        private List<Wz_Node> GetSearchRoots()
        {
            if (comboScope.SelectedIndex == 1)
            {
                Wz_Node selected = context.SelectedNode3 ?? context.SelectedNode2 ?? context.SelectedNode1;
                return selected == null ? new List<Wz_Node>() : new List<Wz_Node> { selected };
            }

            return context.LoadedWz
                .Where(wz => wz?.WzNode != null)
                .Select(wz => wz.WzNode)
                .ToList();
        }

        private void SetSearching(bool searching)
        {
            buttonFile.Enabled = !searching;
            buttonClipboard.Enabled = !searching;
            buttonSearch.Enabled = !searching;
            buttonCancel.Enabled = searching;
            comboScope.Enabled = !searching;
            numericThreshold.Enabled = !searching;
            numericSizeTolerance.Enabled = !searching && !checkIgnoreSize.Checked;
            checkIgnoreSize.Enabled = !searching;
            checkIncludeSpine.Enabled = !searching;
            numericSpineSamples.Enabled = !searching && checkIncludeSpine.Checked;
            numericMaxResults.Enabled = !searching;
            progressBar.Style = searching ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
            if (!searching)
            {
                progressBar.Value = 0;
            }
        }

        private void UpdateProgress(SearchProgress progress)
        {
            if (IsDisposed)
            {
                return;
            }

            labelStatus.Text = $"검색 중: IMG {progress.ImagesScanned}, Spine {progress.SpinesScanned}, 비교 {progress.CandidatesCompared}, 결과 {progress.ResultsFound} - {progress.CurrentPath}";
        }

        private void ShowResults(List<SearchResult> results)
        {
            listResults.BeginUpdate();
            try
            {
                listResults.Items.Clear();
                foreach (SearchResult result in results)
                {
                    ListViewItem item = new ListViewItem(result.Score.ToString("F2"));
                    item.SubItems.Add(string.IsNullOrEmpty(result.Detail) ? result.Kind : result.Kind + ": " + result.Detail);
                    item.SubItems.Add($"{result.Width} x {result.Height}");
                    item.SubItems.Add(result.FullPath);
                    item.Tag = result;
                    listResults.Items.Add(item);
                }
            }
            finally
            {
                listResults.EndUpdate();
            }
        }
    }
}
