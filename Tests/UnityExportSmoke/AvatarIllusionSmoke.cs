using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using WzComparerR2;
using WzComparerR2.Avatar.Export;
using WzComparerR2.AvatarCommon;
using WzComparerR2.PluginBase;
using WzComparerR2.WzLib;
using WzUnity;

internal static class AvatarIllusionSmoke
{
    public static void Run(string output)
    {
        using var pixels = new PixelFile(new byte[]
        {
            0, 0, 0, 0, 10, 20, 30, 255,
            40, 50, 60, 128, 70, 80, 90, 255
        });
        var png = new Wz_Png(2, 2, (int)pixels.FileStream.Length, Wz_TextureFormat.ARGB8888,
            0, 0, 0, 0, new Wz_Image("pixels.img", (int)pixels.FileStream.Length, 0, 0, 0, pixels));
        var root = new Wz_Node("Character");
        var body = Item(root, 2000);
        var head = Item(root, 12000);
        var folder = root.Nodes.Add("Ring");
        var ring = Item(folder, 1116125, 0);
        var stand = ring.Nodes.Add("stand1");
        stand.Nodes.Add("delay").Value = 150;
        stand.Nodes.Add("repeat").Value = 1;
        var first = stand.Nodes.Add("0"); first.Value = png;
        first.Nodes.Add("origin").Value = new Wz_Vector(-3, 11);
        stand.Nodes.Add("1").Value = new Wz_Uol("0");
        ring.Nodes.Add("sit").Value = new Wz_Uol("stand1");
        var prone = ring.Nodes.Add("prone");
        prone.Nodes.Add("delay").Value = 0;
        var proneFrame = prone.Nodes.Add("0"); proneFrame.Value = png;
        proneFrame.Nodes.Add("origin").Value = new Wz_Vector(5, 7);
        var hidden = Item(folder, 1114500);
        var normal = Item(folder, 1112000);
        var paths = Walk(root).ToDictionary(node => node.FullPathToFile, StringComparer.OrdinalIgnoreCase);
        var findEvent = typeof(PluginManager).GetEvent("WzFileFinding", BindingFlags.Static | BindingFlags.NonPublic);
        FindWzEventHandler find = (sender, args) =>
        {
            if (args.FullPath != null && paths.TryGetValue(args.FullPath.Replace('/', '\\'), out var node)) args.WzNode = node;
        };
        findEvent.GetAddMethod(true).Invoke(null, new object[] { find });
        try
        {
            var avatar = new AvatarCanvas
            {
                Body = new AvatarPart(body), Head = new AvatarPart(head),
                Ring1 = new AvatarPart(ring) { Visible = false }, Ring2 = new AvatarPart(hidden) { Visible = false },
                Ring3 = new AvatarPart(normal)
            };
            var exporter = new UnityAvatarExporter(avatar);
            Smoke.Assert(exporter.RendersIllusionRing && exporter.ActiveIllusionRingId == 1116125,
                "An equipped original grade-zero ring did not activate independently of its UI visibility flag");
            var manifest = exporter.Export(output + "-illusion", new[] { "stand1", "stand2", "sit", "prone" });
            var entity = manifest.entities.Single();
            Smoke.Assert(entity.sourcePath == ring.FullPathToFile && entity.equipment.Count == 5
                && entity.equipment.Single(item => item.slot == "Ring2").visible == false,
                "Ring rendering replaced or discarded the complete underlying inventory");
            Smoke.Assert(entity.clips.Count == 4 && entity.clips.All(clip => clip.tracks.Count == 1
                && clip.tracks[0].kind == "body" && clip.tracks[0].itemId == "1116125"),
                "Ring rendering retained human parts or invented separate face layers");
            var idle = entity.clips.Single(clip => clip.name == "stand1");
            var alias = entity.clips.Single(clip => clip.name == "stand2");
            Smoke.Assert(idle.durationMs == 300 && idle.loop && idle.tracks[0].frames.All(frame => frame.delayMs == 150)
                && alias.tracks[0].frames.Select(frame => frame.assetId).SequenceEqual(idle.tracks[0].frames.Select(frame => frame.assetId)),
                "Original parent timing or stand2's documented stand1 alias changed");
            Smoke.Assert(idle.tracks[0].frames.All(frame => frame.originX == -3 && frame.originY == 11)
                && entity.clips.Single(clip => clip.name == "sit").durationMs == 300,
                "Linked frame/action origins or timing were lost");
            Smoke.Assert(entity.clips.Single(clip => clip.name == "prone").durationMs == 120
                && manifest.warnings.Count == 1, "Zero parent delay did not receive the native default with one warning");
            using var expected = png.ExtractPng();
            using var encoded = new MemoryStream(); expected.Save(encoded, ImageFormat.Png);
            string expectedHash = Convert.ToHexString(SHA256.HashData(encoded.ToArray())).ToLowerInvariant();
            Smoke.Assert(manifest.assets.Single().sha256 == expectedHash,
                "A complete ring canvas was recomposed, resized or recolored");
            using var actual = new Bitmap(Path.Combine(output + "-illusion", manifest.assets.Single().file));
            for (int y = 0; y < expected.Height; y++) for (int x = 0; x < expected.Width; x++)
                Smoke.Assert(actual.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb(), "Original ring RGBA changed");
            Smoke.Assert(!avatar.Ring1.Visible && new UnityAvatarExporter(avatar).RendersIllusionRing,
                "A UI-hidden equipped illusion ring lost its automatic appearance");
            hidden.Nodes["info"].Nodes.Add("illusionGrade").Value = 1;
            bool ambiguous = false;
            try { _ = new UnityAvatarExporter(avatar); } catch (InvalidOperationException) { ambiguous = true; }
            Smoke.Assert(ambiguous, "Multiple active illusion rings received an invented priority");
            hidden.Nodes["info"].Nodes["illusionGrade"].Value = -1;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { new UnityAvatarExporter(avatar).Export(output + "-cancelled-illusion", new[] { "stand1" }, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            Smoke.Assert(stopped && !Directory.Exists(output + "-cancelled-illusion"), "Cancelled ring export published a partial destination");
            Console.WriteLine("AVATAR_ILLUSION_PASS gradeZero/hidden/multiple, sourceRGBA/origin, UOL, parentDelay/zeroDelay, alias/noFace, inventory/cancel");
        }
        finally { findEvent.GetRemoveMethod(true).Invoke(null, new object[] { find }); }
    }

    public static void RunOriginal(Func<string, Wz_Node> find, string output)
    {
        var avatar = new AvatarCanvas();
        avatar.AddPart(find("Character/00002000.img"));
        avatar.AddPart(find("Character/00012000.img"));
        avatar.AddPart(find("Character/Ring/01116126.img"));
        var manifest = new UnityAvatarExporter(avatar).Export(output, new[] { "stand1", "stand2", "sit", "prone" });
        var entity = manifest.entities.Single();
        Smoke.Assert(entity.metadata.Any(item => item.key == "rendering/mode" && item.value == "illusion-ring")
            && entity.sourcePath.EndsWith("Ring\\01116126.img", StringComparison.Ordinal), "Original ring appearance stayed human");
        var idle = entity.clips.Single(clip => clip.name == "stand1").tracks.Single();
        Smoke.Assert(idle.frames.Count == 6 && idle.frames.All(frame => frame.delayMs == 150)
            && idle.frames[0].originX == 38 && idle.frames[0].originY == 79, "Original illusion stand clock/origin changed");
        var prone = entity.clips.Single(clip => clip.name == "prone").tracks.Single();
        Smoke.Assert(prone.frames.Count == 1 && prone.frames[0].originX == 39 && prone.frames[0].originY == 55,
            "Original illusion prone frame/origin changed");
        Smoke.Assert(entity.clips.All(clip => clip.tracks.All(track => track.kind != "face")), "Human eyes leaked into a complete ring canvas");
        Console.WriteLine("ORIGINAL_ILLUSION_PASS item=1116126 stand1=6x150 sit/prone=original inventory=preserved");
    }

    private static Wz_Node Item(Wz_Node parent, int id, int? grade = null)
    {
        var node = parent.Nodes.Add(id.ToString("D8") + ".img");
        var info = node.Nodes.Add("info"); info.Nodes.Add("islot").Value = id / 10000 == 111 ? "Ri" : "Bd";
        if (grade.HasValue) info.Nodes.Add("illusionGrade").Value = grade.Value;
        return node;
    }
    private static IEnumerable<Wz_Node> Walk(Wz_Node node)
    {
        yield return node;
        foreach (var child in node.Nodes) foreach (var descendant in Walk(child)) yield return descendant;
    }
    private sealed class PixelFile : IMapleStoryFile
    {
        public PixelFile(byte[] bgra)
        {
            var bytes = new MemoryStream(); bytes.WriteByte(0); bytes.WriteByte(0x78); bytes.WriteByte(0x9c);
            using (var compressed = new DeflateStream(bytes, CompressionLevel.Optimal, true)) compressed.Write(bgra);
            bytes.Position = 0; FileStream = bytes;
        }
        public Wz_Structure WzStructure => null;
        public Stream FileStream { get; }
        public object ReadLock { get; } = new();
        public void Dispose() => FileStream.Dispose();
    }
}
