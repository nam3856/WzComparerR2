using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using WzComparerR2;
using WzComparerR2.Animation;
using WzComparerR2.Controls;
using WzComparerR2.MapRender;
using WzComparerR2.MapRender.Export;
using WzComparerR2.MapRender.Patches2;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

internal static class MapSmoke
{
    public static void Run(string basePath, string output)
    {
        using var game = new RegressionGame(basePath, output);
        game.Run();
        if (game.Failure != null) throw game.Failure;
    }

    private sealed class RegressionGame : Game
    {
        private readonly string basePath;
        private readonly string output;
        public Exception Failure;
        public RegressionGame(string basePath, string output)
        {
            this.basePath = basePath; this.output = Path.GetFullPath(output);
            _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64, GraphicsProfile = GraphicsProfile.HiDef };
            IsFixedTimeStep = false;
        }

        protected override void LoadContent()
        {
            try
            {
                using var data = new DataSource(basePath);
                using var loader = new ResourceLoader(Services) { PatchVisibility = new PatchVisibility() };
                var map = new MapData(new FixedRandom());
                map.Load(data.Find("Map/Map/Map9/993218092.img"), loader);
                map.PreloadResource(loader);
                var options = new MapCompositionExportOptions { SpineBaker = new MapRenderSpineSequenceBaker(GraphicsDevice) };
                var exporter = new MapUnityExporter { GraphicsDevice = GraphicsDevice };
                var animatorList = Animators(map.Scene).ToList();
                foreach (var animator in animatorList.OfType<AnimationItem>()) animator.Update(TimeSpan.FromMilliseconds(173));
                string before = Snapshot(animatorList);
                SetGpuState();
                var gpu = new GpuSnapshot(GraphicsDevice);
                var result = exporter.Export(map, output, options);
                Smoke.Assert(result.map.footholds.Count == 2 && result.map.layers.Count == 6, "Bellona source geometry/visible layers changed");
                Smoke.Assert(before == Snapshot(animatorList), "Export mutated source animator time, selection, frame, or Spine blend state");
                gpu.AssertUnchanged(GraphicsDevice);
                string originalFiles = HashFiles(output);

                using (var cancellation = new CancellationTokenSource())
                {
                    bool cancelled = false;
                    try
                    {
                        exporter.Export(map, output, options, cancellation.Token, new CallbackProgress(value =>
                        {
                            // The first background container has already written real PNG assets.
                            if (value.Phase == "맵 레이어" && value.Completed >= 1) cancellation.Cancel();
                        }));
                    }
                    catch (OperationCanceledException) { cancelled = true; }
                    Smoke.Assert(cancelled, "Map export did not honor cancellation after baking assets");
                }
                Smoke.Assert(originalFiles == HashFiles(output), "Cancelled map export changed the previous complete package");
                Smoke.Assert(before == Snapshot(animatorList), "Cancelled export mutated live source animation");
                gpu.AssertUnchanged(GraphicsDevice);
                Smoke.Assert(!Directory.EnumerateDirectories(Path.GetDirectoryName(output), "." + Path.GetFileName(output) + ".wz-stage-*").Any(), "Cancelled export left staging content");

                foreach (var animator in animatorList.OfType<AnimationItem>()) animator.Update(TimeSpan.FromMilliseconds(431));
                string later = Snapshot(animatorList);
                exporter.Export(map, output, options);
                string repeatedFiles = HashFiles(output);
                Smoke.Assert(originalFiles == repeatedFiles, "Canonical export differs after preview advancement: "
                    + string.Join(", ", originalFiles.Split('\n').Except(repeatedFiles.Split('\n')).Select(value => value.Split(':')[0]).Take(5)));
                Smoke.Assert(later == Snapshot(animatorList), "Repeated export mutated live source animation");
                gpu.AssertUnchanged(GraphicsDevice);
                CheckMotion();
                Console.WriteLine("MAP_REGRESSION_PASS cancellation-after-bake, previous-package-preserved, canonical-repeat, GPU-state-restored, source-animators-unchanged, motion-boundaries");
            }
            catch (Exception error) { Failure = error; }
            finally { Exit(); }
        }

        private void SetGpuState()
        {
            GraphicsDevice.BlendState = BlendState.NonPremultiplied;
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
            GraphicsDevice.SamplerStates[0] = SamplerState.LinearWrap;
            GraphicsDevice.ScissorRectangle = new Rectangle(3, 4, 12, 13);
        }
    }

    private static IEnumerable<object> Animators(SceneNode node)
    {
        if (node is ContainerNode container)
            foreach (var item in container.Slots)
            {
                object animator = item is BackItem back ? back.View?.Animator : item is ObjItem obj ? obj.View?.Animator : item is TileItem tile ? tile.View?.Animator : null;
                if (animator != null) yield return animator;
            }
        foreach (var child in node.Nodes) foreach (var animator in Animators(child)) yield return animator;
    }

    private static string Snapshot(IEnumerable<object> animators)
    {
        var entries = new List<string>();
        foreach (var item in animators)
        {
            if (item is ISpineAnimator spine)
            {
                entries.Add($"spine:{spine.CurrentTime}:{spine.SelectedAnimationName}:{spine.SelectedSkin}");
                var slots = Member(spine.Skeleton, "Slots");
                IEnumerable sequence = slots as IEnumerable ?? Member(slots, "Items") as IEnumerable;
                if (sequence != null)
                    foreach (var slot in sequence)
                    {
                        if (slot == null) continue;
                        entries.Add("slot:" + Member(Member(slot, "Data"), "BlendMode"));
                    }
            }
            else if (item is FrameAnimator frames)
            {
                entries.Add("frame:" + frames.CurrentTime);
                entries.AddRange(frames.Data.Frames.Select(frame => $"{frame.Origin.X},{frame.Origin.Y},{frame.A0},{frame.A1},{frame.Z},{frame.Delay},{frame.Blend}"));
            }
        }
        return string.Join("\n", entries);
    }

    private static object Member(object value, string name)
    {
        if (value == null) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        return value.GetType().GetProperty(name, flags)?.GetValue(value) ?? value.GetType().GetField(name, flags)?.GetValue(value);
    }

    private static string HashFiles(string directory) => string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

    private static void CheckMotion()
    {
        var harmonic = new ObjItem { MoveType = 3 };
        harmonic.View = new ObjItem.ItemView(harmonic);
        harmonic.MoveNodes.Add(new MoveNode { MoveW = 40, MoveH = 20, MoveP = 1000 });
        var encoded = MapCompositionMotionEncoder.EncodeObject(harmonic, 0, 0, new List<MapCompositionMotionWarning>());
        Smoke.Assert(Math.Abs(MapCompositionMotionEncoder.Evaluate(encoded.Tracks.X, 250)) < .001 && Math.Abs(MapCompositionMotionEncoder.Evaluate(encoded.Tracks.Y, 250) - 20) < .001, "Circular motion quarter cycle changed");
        var turnaround = new ObjItem { MoveType = 8, Flip = true };
        turnaround.View = new ObjItem.ItemView(turnaround);
        turnaround.MoveNodes.Add(new MoveNode { MoveW = 100, MoveP = 400, MoveDelay = 100 });
        encoded = MapCompositionMotionEncoder.EncodeObject(turnaround, 0, 0, new List<MapCompositionMotionWarning>());
        foreach (var sample in new[] { (0d, 0d, -100d), (100d, 0d, -100d), (300d, 50d, -100d), (500d, 100d, 100d), (600d, 100d, 100d), (800d, 50d, 100d), (1000d, 0d, -100d) })
            Smoke.Assert(Math.Abs(MapCompositionMotionEncoder.Evaluate(encoded.Tracks.X, sample.Item1) - sample.Item2) < .001 && MapCompositionMotionEncoder.Evaluate(encoded.Tracks.ScaleX, sample.Item1) == sample.Item3, "Turnaround delay/flip boundary changed at " + sample.Item1);
    }

    private sealed class CallbackProgress : IProgress<MapCompositionExportProgress>
    {
        private readonly Action<MapCompositionExportProgress> action;
        public CallbackProgress(Action<MapCompositionExportProgress> action) { this.action = action; }
        public void Report(MapCompositionExportProgress value) => action(value);
    }

    private sealed class GpuSnapshot
    {
        private readonly RenderTargetBinding[] targets;
        private readonly Viewport viewport;
        private readonly Rectangle scissor;
        private readonly BlendState blend;
        private readonly DepthStencilState depth;
        private readonly RasterizerState raster;
        private readonly SamplerState sampler;
        public GpuSnapshot(GraphicsDevice device)
        {
            targets = device.GetRenderTargets(); viewport = device.Viewport; scissor = device.ScissorRectangle;
            blend = device.BlendState; depth = device.DepthStencilState; raster = device.RasterizerState; sampler = device.SamplerStates[0];
        }
        public void AssertUnchanged(GraphicsDevice device)
        {
            Smoke.Assert(targets.SequenceEqual(device.GetRenderTargets()) && viewport.Equals(device.Viewport) && scissor.Equals(device.ScissorRectangle)
                && ReferenceEquals(blend, device.BlendState) && ReferenceEquals(depth, device.DepthStencilState)
                && ReferenceEquals(raster, device.RasterizerState) && ReferenceEquals(sampler, device.SamplerStates[0]), "Export changed the live GraphicsDevice render state");
        }
    }

    private sealed class FixedRandom : IRandom
    {
        public float NextVar(float v, float range, bool nonNegative = false) => v;
        public int NextVar(int v, int range, bool nonNegative = false) => v;
        public Vector2 NextVar(Vector2 v, Vector2 range) => v;
        public Color NextVar(Color v, Color range) => v;
        public int Next(int maxValue) => 0;
        public bool NextPercent(float percent) => false;
    }
}
