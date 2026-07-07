using System;
using DevComponents.DotNetBar;
using WzComparerR2.PluginBase;

namespace WzComparerR2.ImageSearch
{
    public class Entry : PluginEntry
    {
        private FrmImageSearch frm;

        public Entry(PluginContext context)
            : base(context)
        {
        }

        protected override void OnLoad()
        {
            RibbonBar bar = Context.AddRibbonBar("Tools", "Image Search");
            ButtonItem btnItem = new ButtonItem("", "이미지 검색");
            btnItem.Click += BtnItem_Click;
            bar.Items.Add(btnItem);
        }

        private void BtnItem_Click(object sender, EventArgs e)
        {
            if (frm == null || frm.IsDisposed)
            {
                frm = new FrmImageSearch(Context);
                frm.Owner = Context.MainForm;
            }

            frm.Show();
            frm.Focus();
        }

        protected override void OnUnload()
        {
            if (frm != null && !frm.IsDisposed)
            {
                frm.Close();
            }
            base.OnUnload();
        }
    }
}
