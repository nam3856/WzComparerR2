using System;
using System.Collections;
using System.Windows.Forms;
using DevComponents.AdvTree;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

namespace WzComparerR2.ImageSearch
{
    internal sealed class MainFormNavigator
    {
        private readonly PluginContext context;
        private AdvTree advTree1;
        private AdvTree advTree2;

        public MainFormNavigator(PluginContext context)
        {
            this.context = context;
        }

        public bool TrySelect(SearchResult result, out string error)
        {
            error = null;
            if (result == null)
            {
                error = "선택할 결과가 없습니다.";
                return false;
            }

            if (!EnsureControls(out error))
            {
                return false;
            }

            Wz_Node nodeInLeftTree = result.ImageOwnerNode ?? result.Node;
            Node leftTreeNode = FindTreeNodeByWzNode(advTree1.Nodes, nodeInLeftTree);
            if (leftTreeNode == null)
            {
                error = "왼쪽 WZ 트리에서 결과 노드를 찾지 못했습니다.";
                return false;
            }

            if (advTree1.SelectedNode == leftTreeNode)
            {
                advTree1.SelectedNode = null;
            }
            advTree1.SelectedNode = leftTreeNode;
            advTree1.Focus();

            if (result.ImageOwnerNode == null)
            {
                return true;
            }

            if (advTree2.Nodes.Count == 0)
            {
                error = "이미지 노드는 선택했지만 추출된 하위 트리가 비어 있습니다.";
                return false;
            }

            Node imageRoot = advTree2.Nodes[0];
            Node targetNode = string.IsNullOrEmpty(result.RelativePath)
                ? imageRoot
                : FindChildByPath(imageRoot, result.RelativePath.Split('\\'));

            if (targetNode == null)
            {
                advTree2.SelectedNode = imageRoot;
                error = "이미지 노드는 열었지만 세부 하위 노드는 찾지 못했습니다.";
                return false;
            }

            advTree2.SelectedNode = targetNode;
            advTree2.Focus();
            return true;
        }

        private bool EnsureControls(out string error)
        {
            error = null;
            if (advTree1 == null)
            {
                advTree1 = FindAdvTree("advTree1");
            }

            if (advTree2 == null)
            {
                advTree2 = FindAdvTree("advTree2");
            }

            if (advTree1 == null || advTree2 == null)
            {
                error = "WZ 트리 컨트롤을 찾지 못했습니다.";
                return false;
            }

            return true;
        }

        private AdvTree FindAdvTree(string name)
        {
            Control[] controls = context.MainForm.Controls.Find(name, true);
            return controls.Length > 0 ? controls[0] as AdvTree : null;
        }

        private static Node FindTreeNodeByWzNode(IEnumerable nodes, Wz_Node wzNode)
        {
            if (wzNode == null)
            {
                return null;
            }

            foreach (Node node in nodes)
            {
                if (GetWzNode(node) == wzNode)
                {
                    return node;
                }

                Node found = FindTreeNodeByWzNode(node.Nodes, wzNode);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static Node FindChildByPath(Node parent, string[] path)
        {
            Node current = parent;
            foreach (string segment in path)
            {
                if (string.IsNullOrEmpty(segment))
                {
                    continue;
                }

                Node next = null;
                foreach (Node child in current.Nodes)
                {
                    if (string.Equals(child.Text, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        next = child;
                        break;
                    }
                }

                if (next == null)
                {
                    return null;
                }

                current = next;
            }

            return current;
        }

        private static Wz_Node GetWzNode(Node node)
        {
            return (node?.Tag as WeakReference)?.Target as Wz_Node;
        }
    }
}
