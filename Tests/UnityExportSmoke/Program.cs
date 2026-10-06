using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using WzComparerR2;
using WzComparerR2.MapRender;
using WzComparerR2.MapRender.Export;
using WzComparerR2.PluginBase;
using WzComparerR2.UnityExport;
using WzComparerR2.WzLib;
using WzUnity;

internal static class Smoke
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    public static void Assert(bool value, string message) { if (!value) throw new InvalidDataException(message); }

    [STAThread] private static int Main(string[] args)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            SetDllDirectory(Path.Combine(repo, "References/x64"));
            if (args.Length == 0 || args[0] == "synthetic")
            {
                Synthetic(args.Length > 1 ? args[1] : Path.Combine(repo, ".tmp/unity-export-validation/synthetic"));
            }
            else if (args[0] == "data")
            {
                using var data = new DataSource(args[1]);
                ValidateData(data, args[1]);
            }
            else if (args[0] == "export")
            {
                using var game = new ExportGame(args[1], args[2], args.Skip(3).DefaultIfEmpty("993218092").ToArray());
                game.Run();
                if (game.Failure != null) throw game.Failure;
            }
            else if (args[0] == "mapcheck")
            {
                MapSmoke.Run(args[1], args[2]);
            }
            else if (args[0] == "mapgui") MapGuiSmoke.Run(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 993218092);
            else if (args[0] == "inspect")
            {
                using var data = new DataSource(args[1]);
                foreach (string path in args.Skip(2))
                {
                    var node = data.Find(path);
                    Console.WriteLine("INSPECT " + path + " => " + (node == null ? "MISSING" : node.FullPathToFile + " [" + node.Value?.GetType().Name + "]"));
                    if (node == null) continue;
                    foreach (var child in node.Nodes)
                    {
                        Console.WriteLine("  " + child.Text + " [" + child.Value?.GetType().Name + "] " + (child.Value is string || child.Value is int ? child.Value : null));
                        foreach (var nested in child.Nodes.Take(12))
                            Console.WriteLine("    " + nested.Text + " [" + nested.Value?.GetType().Name + "] " + (nested.Value is string || nested.Value is int ? nested.Value : null));
                    }
                }
            }
            else if (args[0] == "avatar")
            {
                using var data = new DataSource(args[1]);
                AvatarSmoke.Run(data.Find, args[2]);
            }
            else if (args[0] == "equipment")
            {
                using var data = new DataSource(args[1]);
                AvatarEquipmentSmoke.RunOriginal(data.Find);
            }
            else if (args[0] == "gui") GuiSmoke.Run(args[1], args[2]);
            else if (args[0] == "reactor")
            {
                using var data = new DataSource(args[1]);
                EntitySmoke.RunReactor(data.Find, args[2]);
            }
            else throw new ArgumentException("synthetic [output] | data <Base.wz> | equipment <Base.wz> | export <Base.wz> <output> [map IDs]");
            Console.WriteLine("SMOKE_PASS");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Synthetic(string output)
    {
        AvatarEquipmentSmoke.Run();
        EntitySmoke.Run(output);
        using (var writer = new UnityExportWriter(output, "synthetic", "mob", "synthetic/origin-delay"))
        using (var bitmap = new Bitmap(8, 12))
        {
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) { graphics.Clear(System.Drawing.Color.Transparent); graphics.FillRectangle(Brushes.Red, 2, 3, 4, 6); }
            string asset = writer.AddBitmap(bitmap);
            Assert(writer.AddBitmap(bitmap) == asset && writer.Manifest.assets.Count == 1, "PNG deduplication failed");
            var entity = new WzEntity { id = "synthetic-mob", kind = "mob", defaultAction = "stand" };
            var track = new WzAnimationTrack { id = "body", kind = "body" };
            track.frames.Add(new WzSpriteFrame { assetId = asset, delayMs = 75, originX = 15, originY = -3, a0 = 128, a1 = 255 });
            track.frames.Add(new WzSpriteFrame { assetId = asset, delayMs = 125, originX = 7, originY = 11 });
            entity.clips.Add(new WzAnimationClip { name = "stand", durationMs = 200, tracks = new() { track } });
            entity.clips.Add(new WzAnimationClip { name = "die", durationMs = 200, loop = false, tracks = new() { track } });
            writer.Manifest.entities.Add(entity);
            writer.Commit();
        }
        string path = Path.Combine(output, "wz-unity.json");
        string prior = File.ReadAllText(path);
        string note = Path.Combine(output, "user-note.txt");
        File.WriteAllText(note, "User-owned export notes.\r\n원본 유지.");
        byte[] noteBytes = File.ReadAllBytes(note);
        var baseline = JsonConvert.DeserializeObject<WzUnityManifest>(prior);
        using (var writer = new UnityExportWriter(output, "synthetic", "mob", "synthetic/origin-delay"))
        {
            foreach (var asset in baseline.assets) writer.AddPngFile(Path.Combine(output, asset.file));
            writer.Manifest.entities.AddRange(baseline.entities);
            writer.Commit();
        }
        Assert(noteBytes.SequenceEqual(File.ReadAllBytes(note)), "Reexport changed a user-owned file");
        prior = File.ReadAllText(path);
        using (var cancel = new CancellationTokenSource())
        using (var writer = new UnityExportWriter(output, "synthetic", "mob", "synthetic", cancel.Token))
        {
            cancel.Cancel();
            bool rejected = false;
            try { writer.Commit(); } catch (OperationCanceledException) { rejected = true; }
            Assert(rejected, "Cancellation did not stop export");
        }
        Assert(prior == File.ReadAllText(path), "Cancelled export changed previous output");
        Assert(noteBytes.SequenceEqual(File.ReadAllBytes(note)), "Cancellation changed a user-owned file");
        using (var writer = new UnityExportWriter(output, "synthetic", "mob", "synthetic"))
        {
            writer.Manifest.assets.Add(new WzPngAsset { id = "escape", file = "../outside.png", width = 1, height = 1 });
            bool rejected = false;
            try { writer.Commit(); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Unsafe reference accepted");
        }
        Assert(prior == File.ReadAllText(path), "Invalid export changed previous output");
        Assert(noteBytes.SequenceEqual(File.ReadAllBytes(note)), "Invalid export changed a user-owned file");
        var parsed = JsonConvert.DeserializeObject<WzUnityManifest>(prior);
        Assert(parsed.entities[0].clips[0].tracks[0].frames[0].originX == 15 && parsed.entities[0].clips[0].durationMs == 200, "Contract round trip lost timing/pivot");
        Console.WriteLine("SYNTHETIC=" + path);
    }

    private static void ValidateData(DataSource data, string basePath)
    {
        foreach (string category in new[] { "String", "Character", "Map", "Mob", "Skill" })
        {
            var node = data.Root.Nodes[category];
            Assert(node != null && node.Nodes.Count > 0, "Empty root: " + category);
            Console.WriteLine($"ROOT {category} children={node.Nodes.Count}");
        }
        var equip = data.Find("String\\Eqp.img");
        Assert(equip != null && equip.Nodes.Count > 0, "Eqp.img extraction failed");
        var name = equip.FindNodeByPath("Eqp\\Cap\\1000000\\name") ?? equip.FindNodeByPath("Cap\\1000000\\name");
        Console.WriteLine("KOREAN_ITEM=" + name?.GetValue<string>());
        Assert(name?.GetValue<string>()?.Any(c => c >= 0xAC00 && c <= 0xD7A3) == true, "Expected Korean item name");
        foreach (string path in new[] { "Character\\00002000.img", "Character\\00012000.img", "Map\\Map\\Map9\\993218092.img" })
        {
            var node = data.Find(path);
            Assert(node != null && node.Nodes.Count > 0, "Image extraction failed: " + path);
            Console.WriteLine($"IMAGE {path} children={node.Nodes.Count}");
        }
        string dataFolder = Directory.GetParent(Path.GetDirectoryName(basePath)).FullName;
        foreach (string prefix in new[] { "Mob", "Skill" })
        {
            string path = Path.Combine(dataFolder, "Packs", prefix + "_00000.ms");
            var pack = new Wz_Structure();
            try
            {
                pack.LoadMsFile(path);
                var image = Walk(pack.WzNode).Select(n => n.Value).OfType<Wz_Image>().FirstOrDefault();
                Assert(image != null && image.TryExtract(), "MS image extraction failed: " + path);
                Console.WriteLine($"MS {prefix}: {image.Name} children={image.Node.Nodes.Count}");
            }
            finally { pack.Clear(); }
        }
    }

    internal static IEnumerable<Wz_Node> Walk(Wz_Node root)
    {
        yield return root;
        foreach (var child in root.Nodes) foreach (var n in Walk(child)) yield return n;
    }

    private sealed class ExportGame : Game
    {
        private readonly string basePath, output;
        private readonly string[] maps;
        public Exception Failure { get; private set; }
        public ExportGame(string basePath, string output, string[] maps)
        {
            this.basePath = basePath; this.output = output; this.maps = maps;
            _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64, GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
            IsFixedTimeStep = false;
        }
        protected override void LoadContent()
        {
            try
            {
                using var data = new DataSource(basePath);
                UnitySpineExportBridge.Register();
                foreach (string mapId in maps)
                {
                    string path = $"Map\\Map\\Map{mapId[0]}\\{mapId}.img";
                    var node = data.Find(path) ?? throw new FileNotFoundException(path);
                    var visibility = new PatchVisibility();
                    using var loader = new ResourceLoader(Services) { PatchVisibility = visibility };
                    var map = new MapData(new TestRandom());
                    map.Load(node, loader); map.PreloadResource(loader);
                    var options = new MapCompositionExportOptions { FrameRate = 30, DisplayMode = 0, Visibility = visibility, SpineBaker = new MapRenderSpineSequenceBaker(GraphicsDevice) };
                    var manifest = new MapUnityExporter { GraphicsDevice = GraphicsDevice }.Export(map, Path.Combine(output, "map-" + mapId), options, default,
                        new InlineProgress(p => Console.WriteLine($"MAP {mapId} {p.Phase} {p.Completed}/{p.Total}")));
                    Console.WriteLine($"MAP_DONE {mapId} assets={manifest.assets.Count} entities={manifest.entities.Count} layers={manifest.map.layers.Count} footholds={manifest.map.footholds.Count} placements={manifest.map.placements.Count} warnings={manifest.warnings.Count}");
                }
                foreach (var entry in new[] { ("mob", "Mob\\0100100.img"), ("npc", "Npc\\1012000.img") })
                {
                    var node = data.Find(entry.Item2);
                    if (node == null) continue;
                    string id = entry.Item1 + "-" + Path.GetFileNameWithoutExtension(node.Text);
                    using var writer = new UnityExportWriter(Path.Combine(output, id), id, entry.Item1, entry.Item2);
                    var entity = UnityEntityExporter.AddEntity(node, entry.Item1, writer, PluginManager.FindWz, graphicsDevice: GraphicsDevice);
                    Assert(entity != null && entity.clips.Count > 0, "No entity clips: " + entry.Item2);
                    writer.Commit();
                    Console.WriteLine($"ENTITY_DONE {id} clips={entity.clips.Count} assets={writer.Manifest.assets.Count}");
                }
            }
            catch (Exception ex) { Failure = ex; }
            finally { Exit(); }
        }
    }

    private sealed class InlineProgress : IProgress<MapCompositionExportProgress>
    {
        private readonly Action<MapCompositionExportProgress> action;
        public InlineProgress(Action<MapCompositionExportProgress> action) => this.action = action;
        public void Report(MapCompositionExportProgress value) => action(value);
    }
    private sealed class TestRandom : IRandom
    {
        private readonly Random random = new Random(1);
        public float NextVar(float v, float range, bool nonNegative = false) { var n = v + (float)(random.NextDouble() * 2 - 1) * range; return nonNegative ? Math.Max(0, n) : n; }
        public int NextVar(int v, int range, bool nonNegative = false) { int n = range == 0 ? v : random.Next(v - range, v + range + 1); return nonNegative ? Math.Max(0, n) : n; }
        public Vector2 NextVar(Vector2 v, Vector2 r) => new Vector2(NextVar(v.X, r.X), NextVar(v.Y, r.Y));
        public Microsoft.Xna.Framework.Color NextVar(Microsoft.Xna.Framework.Color v, Microsoft.Xna.Framework.Color r) => new Microsoft.Xna.Framework.Color(NextVar(v.R, r.R, true), NextVar(v.G, r.G, true), NextVar(v.B, r.B, true), NextVar(v.A, r.A, true));
        public int Next(int maxValue) => random.Next(maxValue);
        public bool NextPercent(float percent) => random.NextDouble() < percent;
    }
}

internal sealed class DataSource : IDisposable
{
    private readonly Wz_Structure structure = new Wz_Structure();
    private readonly EventInfo findEvent;
    private readonly FindWzEventHandler handler;
    public Wz_Node Root => structure.WzNode;
    public DataSource(string basePath)
    {
        Console.WriteLine("LOAD=" + basePath);
        ImgNameContainer.Load();
        structure.LoadKMST1125DataWz(basePath);
        string packs = Path.Combine(Directory.GetParent(Path.GetDirectoryName(basePath)).FullName, "Packs");
        if (Directory.Exists(packs))
        {
            foreach (string pack in Directory.EnumerateFiles(packs).Where(p => p.EndsWith(".ms", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".mn", StringComparison.OrdinalIgnoreCase)))
                structure.LoadMsFile(pack);
        }
        findEvent = typeof(PluginManager).GetEvent("WzFileFinding", BindingFlags.Static | BindingFlags.NonPublic);
        handler = (sender, args) => args.WzNode = !string.IsNullOrWhiteSpace(args.FullPath) ? Find(args.FullPath) : Root.Nodes[args.WzType.ToString()];
        findEvent.GetAddMethod(true).Invoke(null, new object[] { handler });
        Console.WriteLine("LOADED=" + string.Join(",", Root.Nodes.Select(n => n.Text)));
    }
    public Wz_Node Find(string path)
    {
        path = path.Replace('/', '\\').TrimStart('\\');
        if (path.StartsWith("Base\\", StringComparison.OrdinalIgnoreCase)) path = path.Substring(5);
        var node = Root.FindNodeByPath(path, true);
        if (node?.Value is Wz_Image image)
        {
            if (!image.TryExtract(out var error)) throw new InvalidDataException("Cannot extract " + path, error);
            return image.Node;
        }
        return node;
    }
    public void Dispose()
    {
        if (findEvent != null) findEvent.GetRemoveMethod(true).Invoke(null, new object[] { handler });
        structure.Clear();
    }
}
