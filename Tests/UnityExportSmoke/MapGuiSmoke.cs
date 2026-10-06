using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WzComparerR2;
using WzComparerR2.Common;
using WzComparerR2.Config;
using WzComparerR2.MapRender;
using WzComparerR2.WzLib;
using Color = Microsoft.Xna.Framework.Color;

internal static class MapGuiSmoke
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Run(string basePath, string output, int mapId = 993218092)
    {
        typeof(WzComparerR2.Program).Assembly.GetType("WzComparerR2.Dotnet6Patch").GetMethod("Patch").Invoke(null, null);
        ConfigManager.RegisterAllSection(typeof(WzComparerR2.Program).Assembly);
        ConfigManager.RegisterAllSection(typeof(FrmMapRender2).Assembly);
        using var data = new DataSource(basePath);
        using var preview = new FrmMapRender2 { StringLinker = new StringLinker() };
        var observer = new PreviewObserver(preview, output, mapId);
        preview.Components.Add(observer);
        preview.LoadMap(data.Find($"Map/Map/Map{mapId / 100000000}/{mapId:D9}.img").GetNodeWzImage());
        preview.Run();
        if (observer.Failure != null) throw observer.Failure;
        Smoke.Assert(observer.Completed, "Native map preview ended before rendering verification");
    }

    private sealed class PreviewObserver : DrawableGameComponent
    {
        private readonly string output;
        private readonly int mapId;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private int renderedFrames;
        private int menuFrames;
        private bool menuShown;
        private bool checkResumedElapsed;
        public Exception Failure;
        public bool Completed;
        public PreviewObserver(FrmMapRender2 preview, string output, int mapId) : base(preview) { this.output = Path.GetFullPath(output); this.mapId = mapId; }

        public override void Update(GameTime gameTime)
        {
            base.Update(gameTime);
            if (!checkResumedElapsed) return;
            checkResumedElapsed = false;
            if (gameTime.ElapsedGameTime.TotalMilliseconds >= 150)
            {
                Failure = new InvalidDataException("A blocking export pause was included in resumed animation elapsed time");
                RequestClose();
                return;
            }
            Console.WriteLine("MAP_PAUSE_PASS resumedElapsedMs=" + gameTime.ElapsedGameTime.TotalMilliseconds);
        }

        public override void Draw(GameTime gameTime)
        {
            try
            {
                base.Draw(gameTime);
                if (elapsed.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Native map preview did not render the map and export menu within 45 seconds");
                var map = Field("mapData") as MapData;
                if (map?.ID != mapId || (float)Field("opacity") < .99f) return;
                renderedFrames++;
                if (renderedFrames < 12) return;
                if (!menuShown)
                {
                    Smoke.Assert(map.Scene.Back.Slots.Count > 0, "Native map renderer has no source background layers");
                    if (mapId == 200090010) MapScrollRegression.Check((FrmMapRender2)Game, map);
                    Capture("native-map-preview.png");
                    object options = typeof(FrmMapRender2).GetProperty("UIOptionsInstance", Flags).GetValue(Game);
                    var tabs = Children(Property(options, "Content")).Single(child => child.GetType().Name == "TabControl");
                    var items = ((IEnumerable)Property(tabs, "ItemsSource")).Cast<object>();
                    var unityTab = items.Single(item => (string)Property(item, "Header") == "Unity 추출");
                    var button = Children(Property(unityTab, "Content")).Single(child => child.GetType().Name == "Button");
                    Smoke.Assert((string)Property(button, "Content") == "Unity 맵 내보내기 (Ctrl+E)", "Native Unity map export button is missing");
                    tabs.GetType().GetProperty("SelectedItem").SetValue(tabs, unityTab);
                    options.GetType().GetMethod("Toggle").Invoke(options, null);
                    menuShown = true;
                    if (mapId == 200090010)
                    {
                        Thread.Sleep(300); // Simulate a blocking export on the native render thread.
                        Game.ResetElapsedTime();
                        checkResumedElapsed = true;
                    }
                    return;
                }
                if (++menuFrames < 12) return;
                Capture("native-map-unity-menu.png");
                Completed = true;
                Console.WriteLine($"MAP_GUI_PASS map={mapId} renderedFrames={renderedFrames} unityExportButton=visible screenshots={output}");
                RequestClose();
            }
            catch (Exception error) { Console.Error.WriteLine("MAP_GUI_INNER_ERROR " + error); Failure = error; RequestClose(); }
        }

        private void RequestClose()
        {
            Visible = false;
            var window = (System.Windows.Forms.Form)System.Windows.Forms.Form.FromHandle(Game.Window.Handle);
            window.BeginInvoke((Action)(() => window.Close()));
        }

        private object Field(string name) => typeof(FrmMapRender2).GetField(name, Flags).GetValue(Game);

        private void Capture(string name)
        {
            int width = GraphicsDevice.PresentationParameters.BackBufferWidth;
            int height = GraphicsDevice.PresentationParameters.BackBufferHeight;
            var pixels = new Color[width * height];
            GraphicsDevice.GetBackBufferData(pixels);
            Smoke.Assert(pixels.Select(pixel => pixel.PackedValue).Distinct().Take(100).Count() == 100, "Native map backbuffer is blank");
            Directory.CreateDirectory(output);
            using var texture = new Texture2D(GraphicsDevice, width, height);
            texture.SetData(pixels);
            using var stream = File.Create(Path.Combine(output, name));
            texture.SaveAsPng(stream, width, height);
        }
    }

    private static object Property(object source, string name) => source.GetType().GetProperty(name).GetValue(source);
    private static IEnumerable<object> Children(object parent) => ((IEnumerable)Property(parent, "Children")).Cast<object>();
}
