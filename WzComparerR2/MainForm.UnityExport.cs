using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using DevComponents.AdvTree;
using WzComparerR2.PluginBase;
using WzComparerR2.UnityExport;
using WzComparerR2.WzLib;

namespace WzComparerR2
{
    public partial class MainForm
    {
        private void InitializeUnityExportMenu()
        {
            foreach (var menu in new[] { contextMenuStrip1, contextMenuStrip2 })
            {
                var item = new ToolStripMenuItem("몬스터·NPC·리액터 Unity 추출");
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(item);
                menu.Opening += (s, e) => item.Enabled = GetUnityEntitySelection(menu, out _, out _);
                item.Click += (s, e) => ExportSelectedUnityEntity(menu);
            }
        }

        private bool GetUnityEntitySelection(ContextMenuStrip menu, out Wz_Image image, out string kind)
        {
            var tree = menu.SourceControl as AdvTree;
            var node = (tree?.SelectedNode ?? (menu == contextMenuStrip2 ? advTree3.SelectedNode : advTree1.SelectedNode)).AsWzNode();
            image = node?.Value as Wz_Image ?? node?.GetNodeWzImage();
            kind = null;
            if (image == null) return false;
            var type = node.GetNodeWzFile()?.Type;
            if (type == Wz_Type.Mob) kind = "mob";
            else if (type == Wz_Type.Npc) kind = "npc";
            else if (type == Wz_Type.Reactor) kind = "reactor";
            else
            {
                string path = (node.FullPathToFile ?? "").Replace('/', '\\');
                foreach (string candidate in new[] { "Mob", "Npc", "Reactor" })
                    if (path.Split('\\').Any(p => p.Equals(candidate, StringComparison.OrdinalIgnoreCase) || p.StartsWith(candidate + "_", StringComparison.OrdinalIgnoreCase)))
                        kind = candidate.ToLowerInvariant();
            }
            return kind != null;
        }

        private void ExportSelectedUnityEntity(ContextMenuStrip menu)
        {
            if (!GetUnityEntitySelection(menu, out var image, out var kind)) return;
            using (var folder = new FolderBrowserDialog { Description = "Unity 추출 결과의 상위 폴더를 선택하세요. 개체별 하위 폴더가 생성됩니다." })
            {
                if (folder.ShowDialog(this) != DialogResult.OK) return;
                string id = kind + "-" + Path.GetFileNameWithoutExtension(image.Name);
                string destination = Path.Combine(folder.SelectedPath, id);
                int warningCount = 0;
                try
                {
                    if (!image.TryExtract(out var error)) throw new InvalidDataException("이미지를 열 수 없습니다: " + image.Name, error);
                    var device = pictureBoxEx1.GraphicsDevice;
                    bool completed = UnityExportDialog.Run(this, token =>
                    {
                        // Preview and GPU baking share the same device.
                        lock (device)
                        using (var writer = new UnityExportWriter(destination, id, kind, image.Node.FullPathToFile, token))
                        {
                            var entity = UnityEntityExporter.AddEntity(image.Node, kind, writer, PluginManager.FindWz, id, device);
                            if (entity == null) throw new InvalidDataException("지원되는 애니메이션이 없습니다.\r\n" + string.Join("\r\n", writer.Manifest.warnings.Select(w => w.reason)));
                            writer.Commit();
                            warningCount = writer.Manifest.warnings.Count;
                        }
                    });
                    if (completed) MessageBox.Show(this, (warningCount == 0 ? "Unity 추출 완료" : $"경고 {warningCount}개와 함께 Unity 추출 완료") + "\r\n" + destination + "\r\nUnity의 Tools > WZ Importer 메뉴에서 가져오세요.", "Unity 추출");
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unity 추출 실패", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
