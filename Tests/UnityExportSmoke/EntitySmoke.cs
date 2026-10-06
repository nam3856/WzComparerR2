using System.Drawing;
using WzComparerR2.UnityExport;
using WzComparerR2.WzLib;
using WzUnity;

internal static class EntitySmoke
{
    public static void RunReactor(Func<string, Wz_Node> find, string output)
    {
        var root = find("Reactor") ?? throw new FileNotFoundException("Reactor root");
        foreach (var entry in root.Nodes.OrderBy(node => node.Text, StringComparer.Ordinal))
        {
            if (!entry.Text.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) continue;
            var source = find("Reactor/" + entry.Text);
            var state = source?.Nodes["0"];
            var first = UnityEntityExporter.ResolveUol(state?.Nodes["0"], (path, file) => find(path));
            if (!(first?.Value is Wz_Png)) continue;
            string id = "reactor-" + Path.GetFileNameWithoutExtension(entry.Text);
            using var writer = new UnityExportWriter(Path.Combine(output, id), id, "reactor", source.FullPathToFile);
            var entity = UnityEntityExporter.AddEntity(source, "reactor", writer, (path, file) => find(path));
            Smoke.Assert(entity != null && entity.clips.Any(clip => clip.name == "0"), "Real reactor state zero missing");
            var frames = entity.clips.Single(clip => clip.name == "0").tracks[0].frames;
            int expectedCount = state.Nodes.Count(node => int.TryParse(node.Text, out _));
            if (state.Nodes["zigzag"]?.Value is int zigzag && zigzag != 0 && expectedCount > 2) expectedCount = expectedCount * 2 - 2;
            Smoke.Assert(frames.Count == expectedCount, "Real reactor state frame count changed");
            int delay = first.Nodes["delay"]?.Value is int value ? value : 120;
            Smoke.Assert(frames[0].delayMs == (delay == 0 ? 120 : Math.Abs((double)delay)), "Real reactor frame delay changed");
            writer.Commit();
            Console.WriteLine($"REACTOR_PASS id={id} sourceFrames={expectedCount} clips={entity.clips.Count} assets={writer.Manifest.assets.Count} warnings={writer.Manifest.warnings.Count}");
            return;
        }
        throw new InvalidDataException("No real reactor state zero PNG fixture found");
    }

    public static void Run(string output)
    {
        using var writer = new UnityExportWriter(output + "-entity", "entity-regression", "mob", "synthetic/entity");
        AssertMalformed(writer, new Wz_Node("0"), Png("1"));
        AssertMalformed(writer, Png("0"), new Wz_Node("1") { Value = new Wz_Uol("missing") }, Png("2"));
        var metadata = new Wz_Node("event");
        metadata.Nodes.Add(new Wz_Node("0") { Value = 1 });
        metadata.Nodes.Add(new Wz_Node("1") { Value = "reactor event" });
        Smoke.Assert(UnityEntityExporter.ReadTrack(metadata, "body", writer, null).frames.Count == 0,
            "Numeric reactor metadata was misclassified as an animation");
        var wrapped = new Wz_Node("state");
        var first = wrapped.Nodes.Add("0");
        first.Nodes.Add(new Wz_Node("0") { Value = 5 });
        wrapped.Nodes.Add(new Wz_Node("1") { Value = 9 });
        Smoke.Assert(UnityEntityExporter.ReadTrack(wrapped, "body", writer, null).frames.Count == 0,
            "Nested reactor metadata was misclassified as an animation");

        var previousBaker = UnityEntityExporter.SpecialAnimationExporter;
        try
        {
            using var image = new Bitmap(2, 3);
            string asset = writer.AddBitmap(image);
            var source = new Wz_Node("0000001.img");
            var attack = source.Nodes.Add("attack1");
            attack.Nodes.Add("spine").Value = "body";
            var info = attack.Nodes.Add("info");
            info.Nodes.Add("effectAfter").Value = 125;
            var effect = info.Nodes.Add("effect");
            effect.Nodes.Add("spine").Value = "effect";
            int bodyCalls = 0, effectCalls = 0;
            UnityEntityExporter.SpecialAnimationExporter = (node, target, find, device) =>
            {
                if (ReferenceEquals(node, attack))
                {
                    bodyCalls++;
                    return new() { Clip("main", 100, asset) };
                }
                Smoke.Assert(ReferenceEquals(node, effect), "Wrong Spine effect source");
                effectCalls++;
                return new() { Clip("flare", 80, asset), Clip("spark", 200, asset) };
            };
            var entity = UnityEntityExporter.AddEntity(source, "mob", writer, null);
            Smoke.Assert(bodyCalls == 1 && effectCalls == 1 && entity.clips.Count == 2,
                "Supported Spine effects or variants were dropped");
            foreach (var clip in entity.clips)
            {
                Smoke.Assert(!clip.loop && clip.tracks.All(track => !track.loop), "Attack Spine clips changed into loops");
                var effectTrack = clip.tracks.Single(track => track.kind == "effect");
                Smoke.Assert(effectTrack.startMs == 125 && effectTrack.frames[0].drawOrder == 1,
                    "Spine effect lost original start time or draw order");
                Smoke.Assert(clip.durationMs == 125 + effectTrack.frames[0].delayMs,
                    "Delayed effect was truncated at the body's completion");
            }
            writer.Commit();
            Console.WriteLine("ENTITY_REGRESSION malformed-first/middle, reactor-metadata, Spine-effect-variants PASS");
        }
        finally { UnityEntityExporter.SpecialAnimationExporter = previousBaker; }
    }

    private static void AssertMalformed(UnityExportWriter writer, params Wz_Node[] frames)
    {
        var action = new Wz_Node("stand");
        foreach (var frame in frames) action.Nodes.Add(frame);
        bool rejected = false;
        try { UnityEntityExporter.ReadTrack(action, "body", writer, null); }
        catch (InvalidDataException ex) when (ex.Message.Contains("missing or unresolved frame")) { rejected = true; }
        Smoke.Assert(rejected, "Malformed sequence was exported incompletely");
    }

    // Pixel extraction must never be reached in malformed-sequence tests.
    private static Wz_Node Png(string name) => new(name)
    {
        Value = new Wz_Png(1, 1, 0, (Wz_TextureFormat)2, 0, 0, 0, 0, null)
    };

    private static WzAnimationClip Clip(string name, double delay, string asset) => new()
    {
        name = name, durationMs = delay,
        tracks = new() { new() { id = "spine", kind = "spine-sequence", frames = new() { new() { assetId = asset, delayMs = delay } } } }
    };
}
