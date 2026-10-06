using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using WzComparerR2.Avatar;
using WzComparerR2.AvatarCommon;
using WzComparerR2.Common;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;

internal static class GuiSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(string basePath, string output)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var mainAssembly = typeof(WzComparerR2.Program).Assembly;
        mainAssembly.GetType("WzComparerR2.Dotnet6Patch").GetMethod("Patch").Invoke(null, null);
        using var main = (Form)Activator.CreateInstance(mainAssembly.GetType("WzComparerR2.MainForm"));
        Invoke(main, "PluginOnLoad");
        var context = (PluginContext)Activator.CreateInstance(typeof(PluginContext), Flags, null, new object[] { main }, null);
        var entry = new Entry(context);
        Invoke(entry, "OnLoad");
        main.StartPosition = FormStartPosition.Manual;
        main.Location = new Point(20, 20);
        main.Size = new Size(1400, 950);
        main.Show();
        main.BringToFront();
        Pump(250);
        OpenAndVerify(main, basePath);
        var form = Descendants(main).Single(control => control.GetType().FullName == "WzComparerR2.Avatar.UI.AvatarForm");
        var tabs = main.Controls.Find("superTabControl1", true).Single();
        tabs.GetType().GetProperty("SelectedTab").SetValue(tabs, typeof(Entry).GetProperty("Tab").GetValue(entry));
        Invoke(form, "SuspendUpdateDisplay");
        foreach (string part in new[] { "Character/00002000.img", "Character/00012000.img", "Character/Face/00020000.img",
            "Character/Hair/00030000.img", "Character/Coat/01040036.img", "Character/Pants/01060026.img", "Character/Cap/01000070.img" })
            Invoke(form, "LoadPart", PluginManager.FindWz(part) ?? throw new FileNotFoundException(part));
        Invoke(form, "ResumeUpdateDisplay");
        main.StartPosition = FormStartPosition.Manual;
        main.Location = new Point(20, 20);
        main.Size = new Size(1400, 950);
        main.Show();
        main.BringToFront();
        Pump(350);
        Invoke(form, "btnReset_Click", null, EventArgs.Empty);
        Invoke(form, "UpdateDisplay");
        var preview = (Control)Field(form, "avatarContainer1");
        preview.Refresh();
        Pump(250);
        var avatar = (AvatarCanvas)Field(form, "avatar");
        var cache = (IDictionary)Field(preview, "bmpCache");
        Smoke.Assert(main.Visible && form.Visible && preview.Visible && cache.Count > 0 && avatar.Body != null && avatar.Head != null,
            "The native avatar window did not display the real outfit");
        var bar = Field(form, "bar3");
        var items = (IEnumerable)bar.GetType().GetProperty("Items").GetValue(bar);
        var export = items.Cast<object>().Single(item => (string)item.GetType().GetProperty("Name").GetValue(item) == "btnExportUnity");
        Smoke.Assert((bool)export.GetType().GetProperty("Visible").GetValue(export) &&
            (string)export.GetType().GetProperty("Text").GetValue(export) == "캐릭터 Unity 추출", "Avatar Unity toolbar is missing");
        foreach (string name in new[] { "contextMenuStrip1", "contextMenuStrip2" })
        {
            var menu = (ContextMenuStrip)Field(main, name);
            Smoke.Assert(menu.Items.Cast<ToolStripItem>().Any(item => item.Text == "몬스터·NPC·리액터 Unity 추출"),
                "Entity Unity context menu missing: " + name);
        }
        Directory.CreateDirectory(output);
        using (var screenshot = new Bitmap(main.Width, main.Height))
        {
            using var graphics = Graphics.FromImage(screenshot);
            graphics.CopyFromScreen(main.Location, Point.Empty, main.Size);
            screenshot.Save(Path.Combine(output, "native-avatar-window.png"), ImageFormat.Png);
        }
        using (var screenshot = new Bitmap(preview.Width, preview.Height))
        {
            preview.DrawToBitmap(screenshot, new Rectangle(Point.Empty, preview.Size));
            screenshot.Save(Path.Combine(output, "native-avatar-preview.png"), ImageFormat.Png);
        }
        Console.WriteLine($"GUI_PASS avatar={avatar.ActionName}, cache={cache.Count}, entityMenus=2, avatarUnityButton=visible, screenshot={output}");
        main.Hide(); // Dispose without saving normal window layout/settings via FormClosing.
    }

    private static void OpenAndVerify(Form main, string basePath)
    {
        Console.WriteLine("GUI_OPEN_START " + basePath);
        // Match the real menu handler's worker route. No DataSource fallback is registered:
        // every subsequent lookup and avatar part comes through the MainForm's openedWz.
        using var timeout = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("GUI_OPEN_TIMEOUT: actual MainForm.openWz did not finish within 180 seconds");
            Environment.Exit(3);
        }, null, TimeSpan.FromSeconds(180), Timeout.InfiniteTimeSpan);
        var opening = Task.Run(() => Invoke(main, "openWz", basePath));
        var elapsed = Stopwatch.StartNew();
        int nextReport = 10;
        while (!opening.IsCompleted)
        {
            Pump(50);
            if (elapsed.Elapsed.TotalSeconds >= nextReport)
            {
                Console.WriteLine("GUI_OPEN_PROGRESS " + Field(main, "labelItemStatus").GetType().GetProperty("Text").GetValue(Field(main, "labelItemStatus")));
                nextReport += 10;
            }
        }
        opening.GetAwaiter().GetResult();
        var opened = (IList<Wz_Structure>)Field(main, "openedWz");
        Smoke.Assert(opened.Count == 1 && opened[0].WzNode.Nodes.Count > 0, "Actual MainForm WZ opening failed");
        var tree = Field(main, "advTree1");
        var roots = ((IEnumerable)tree.GetType().GetProperty("Nodes").GetValue(tree)).Cast<object>().ToArray();
        Smoke.Assert(roots.Length == 1, "MainForm WZ tree has no opened root");
        var categories = ((IEnumerable)roots[0].GetType().GetProperty("Nodes").GetValue(roots[0])).Cast<object>()
            .Select(node => (string)node.GetType().GetProperty("Text").GetValue(node)).ToArray();
        foreach (string category in new[] { "Character", "Map", "Mob", "Npc", "Reactor", "String" })
            Smoke.Assert(categories.Contains(category) && PluginManager.FindWz(category)?.Nodes.Count > 0,
                "MainForm category lookup missing: " + category);
        var linker = (StringLinker)Field(main, "stringLinker");
        Smoke.Assert(linker.HasValues && linker.StringEqp.TryGetValue(1000000, out var cap)
            && cap.Name.Any(character => character >= 0xAC00 && character <= 0xD7A3), "MainForm StringLinker Korean cap name missing");
        Console.WriteLine($"GUI_OPEN_PASS categories={categories.Length}, KoreanCap={linker.StringEqp[1000000].Name}, eqp={linker.StringEqp.Count}, msPacks={opened[0].ms_files.Count}");
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
}
