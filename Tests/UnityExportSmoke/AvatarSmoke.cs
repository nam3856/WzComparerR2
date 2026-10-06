using System.Drawing;
using System.Drawing.Imaging;
using WzComparerR2.Avatar.Export;
using WzComparerR2.AvatarCommon;
using WzComparerR2.WzLib;
using WzUnity;

internal static class AvatarSmoke
{
    public static void Run(Func<string, Wz_Node> find, string outputRoot)
    {
        var avatar = new AvatarCanvas { EmotionName = "default" };
        foreach (string path in new[]
        {
            "Character/00002000.img", "Character/00012000.img",
            "Character/Face/00020000.img", "Character/Hair/00030000.img",
            "Character/Coat/01040036.img", "Character/Pants/01060026.img"
        })
        {
            Wz_Node part = find(path);
            Smoke.Assert(part != null && avatar.AddPart(part) != null, "Missing avatar fixture: " + path);
        }
        Smoke.Assert(avatar.LoadZ(find("Base/zmap.img")) && avatar.LoadActions(), "Avatar base action/zmap unavailable");
        avatar.LoadEmotions();
        if (avatar.GetFaceFrames("blink").Length > 1) avatar.EmotionName = "blink";
        AddFirstAvailableCapEffect(avatar, find);
        var exporter = new UnityAvatarExporter(avatar);
        bool hairCover = avatar.HairCover;
        avatar.HairCover = !hairCover;
        Smoke.Assert(new UnityAvatarExporter(avatar).AppearanceId != exporter.AppearanceId, "Appearance IDs collide");
        avatar.HairCover = hairCover;
        Smoke.Assert(new UnityAvatarExporter(avatar).AppearanceId == exporter.AppearanceId, "Appearance IDs are unstable");
        string[] actions = new[] { "stand1", "walk1", "alert", "swingO1" };
        string output = Path.Combine(outputRoot, exporter.AppearanceId);
        int cachedBefore = avatar.SkinCache.Count;
        WzUnityManifest manifest = exporter.Export(output, actions);
        Smoke.Assert(avatar.SkinCache.Count == cachedBefore, "Exporter mutated preview skin cache");
        Smoke.Assert(manifest.entities.Count == 1 && manifest.entities[0].clips.Count == actions.Length,
            "Avatar action export incomplete");
        var entity = manifest.entities[0];
        Smoke.Assert(entity.hasEquipmentMetadata && entity.equipment.Count == avatar.Parts.Count(part => part != null),
            "Avatar export lost outfit presence or a configured part");
        foreach (var item in entity.equipment)
            Smoke.Assert(item.itemId == avatar.Parts[item.slotIndex].ID?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && item.visible == avatar.Parts[item.slotIndex].Visible,
                "Avatar equipment identity or visibility changed: " + item.slot);
        foreach (var clip in entity.clips)
        {
            ActionFrame[] sourceFrames = avatar.GetActionFrames(clip.name);
            var clock = clip.tracks.Single(track => track.id == "body");
            Smoke.Assert(clock.frames.Count == sourceFrames.Length, "Body frame count changed: " + clip.name);
            for (int index = 0; index < sourceFrames.Length; index++)
                Smoke.Assert(clock.frames[index].delayMs == (sourceFrames[index].Delay == 0 ? 120 : Math.Abs((long)sourceFrames[index].Delay)),
                    "Original body delay changed: " + clip.name + "/" + index);
            Smoke.Assert(clip.tracks.Any(track => track.slot == "0") && clip.tracks.Any(track => track.slot == "1"),
                "Body/head ownership collapsed: " + clip.name);
            foreach (var track in clip.tracks.Where(track => track.poseTrack == "body"))
            {
                Smoke.Assert(track.poses.Count == sourceFrames.Length * track.frames.Count, "Incomplete independent poses: " + track.id);
                Smoke.Assert(track.poses.All(pose => pose.overrideSprite), "Pose-dependent sprite missing: " + track.id);
            }
            foreach (int time in new[] { 0, 137, 489 })
                ComparePose(avatar, clip, manifest, output, Path.Combine(outputRoot, "avatar-reference"), time);
        }
        if (avatar.EmotionName == "blink")
            Smoke.Assert(entity.clips.Any(clip => clip.tracks.Any(track => track.kind == "face" && track.frames.Count > 1)),
                "Blink was flattened into body timing");
        string marker = Path.Combine(output, "wz-unity.json");
        string beforeCancellation = File.ReadAllText(marker);
        using (var cancel = new CancellationTokenSource())
        {
            bool canceled = false;
            try
            {
                new UnityAvatarExporter(avatar).Export(output, actions, cancel.Token,
                    (current, total, message) => cancel.Cancel());
            }
            catch (OperationCanceledException) { canceled = true; }
            Smoke.Assert(canceled && beforeCancellation == File.ReadAllText(marker), "Canceled avatar export changed previous output");
        }
        avatar.ClearSkinCache();
        Console.WriteLine($"AVATAR_DONE {manifest.id} clips={entity.clips.Count} assets={manifest.assets.Count} warnings={manifest.warnings.Count}");
    }

    private static void AddFirstAvailableCapEffect(AvatarCanvas avatar, Func<string, Wz_Node> find)
    {
        var effects = find("Effect/ItemEff.img");
        if (effects == null) return;
        foreach (var item in effects.Nodes)
        {
            if (!int.TryParse(item.Text, out int id) || id / 10000 != 100) continue;
            var effect = item.Nodes["effect"];
            if (effect == null || (effect.Nodes["stand1"] == null && effect.Nodes["default"] == null)) continue;
            Wz_Node cap = find("Character/Cap/" + id.ToString("D8") + ".img");
            if (cap == null) continue;
            avatar.AddPart(cap);
            if (avatar.GetEffectFrames("stand1", 4).Length > 0)
            {
                Console.WriteLine("AVATAR_EFFECT_CAP=" + id);
                return;
            }
        }
        Console.WriteLine("AVATAR_EFFECT_CAP=unavailable");
    }

    private static void ComparePose(AvatarCanvas avatar, WzAnimationClip clip, WzUnityManifest manifest,
        string bundle, string referenceDirectory, int timeMs)
    {
        var effectFrames = new ActionFrame[AvatarCanvas.LayerSlotLength];
        var effectAlpha = new Dictionary<int, int>();
        foreach (var track in clip.tracks.Where(track => track.kind == "effect"))
        {
            int end = track.id.IndexOf('/');
            int slot = int.Parse(track.id.Substring("effect-".Length, end - "effect-".Length));
            ActionFrame[] source = avatar.GetEffectFrames(clip.name, slot);
            int index = Select(source.Select(frame => Duration(frame.Delay)).ToArray(), timeMs, out double progress);
            effectFrames[slot] = source[index];
            effectAlpha[slot] = Alpha(source[index].A0, source[index].A1, progress);
        }
        ActionFrame[] bodyFrames = avatar.GetActionFrames(clip.name);
        int bodyIndex = Select(bodyFrames.Select(frame => Duration(frame.Delay)).ToArray(), timeMs, out double bodyProgress);
        ActionFrame body = bodyFrames[bodyIndex];
        ActionFrame[] faceFrames = avatar.GetFaceFrames(avatar.EmotionName);
        int faceIndex = Select(faceFrames.Select(frame => Duration(frame.Delay)).ToArray(), timeMs, out double faceProgress);
        ActionFrame face = faceFrames[faceIndex];
        AvatarRenderPrimitive[] primitives = avatar.CreateFramePrimitives(avatar.CreateFrame(body, face, null, effectFrames));
        var actual = new List<(Bitmap image, int x, int y, int z, int order, int alpha)>();
        try
        {
            foreach (var track in clip.tracks.Where(track => track.kind != "clock"))
            {
                int index = Select(track.frames.Select(value => value.delayMs).ToArray(), timeMs, out double progress);
                var frame = track.frames[index];
                var pose = track.poses.FirstOrDefault(value => value.poseFrame == bodyIndex && value.frameIndex == index);
                if (!frame.visible || pose != null && !pose.visible) continue;
                string assetId = pose?.overrideSprite == true ? pose.assetId : frame.assetId;
                var asset = manifest.assets.Single(value => value.id == assetId);
                actual.Add((new Bitmap(Path.Combine(bundle, asset.file)), (int)(pose?.x ?? frame.x),
                    (int)(pose?.y ?? frame.y), pose?.z ?? frame.z, pose?.drawOrder ?? frame.drawOrder, Alpha(frame.a0, frame.a1, progress)));
            }
            Rectangle bounds = Rectangle.Empty;
            foreach (var primitive in primitives)
                bounds = Union(bounds, new Rectangle(primitive.Position, primitive.Bitmap.Size));
            foreach (var item in actual) bounds = Union(bounds, new Rectangle(item.x, item.y, item.image.Width, item.image.Height));
            Smoke.Assert(!bounds.IsEmpty, "Empty avatar render: " + clip.name);
            using var expected = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using var rendered = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(expected))
                foreach (var primitive in primitives)
                {
                    int alpha = primitive.Kind == AvatarRenderPrimitiveKind.IndependentEffect
                        ? effectAlpha[primitive.EffectSlot.Value]
                        : primitive.PartSlot == 2 || primitive.PartSlot == 14
                            ? Alpha(face.A0, face.A1, faceProgress) : Alpha(body.A0, body.A1, bodyProgress);
                    Draw(graphics, primitive.Bitmap, primitive.Position.X - bounds.X, primitive.Position.Y - bounds.Y, alpha);
                }
            using (var graphics = Graphics.FromImage(rendered))
                foreach (var item in actual.OrderBy(item => item.z).ThenBy(item => item.order))
                    Draw(graphics, item.image, item.x - bounds.X, item.y - bounds.Y, item.alpha);
            Directory.CreateDirectory(referenceDirectory);
            string fileName = clip.name + (timeMs == 0 ? "" : "-" + timeMs);
            expected.Save(Path.Combine(referenceDirectory, fileName + "-source.png"), ImageFormat.Png);
            rendered.Save(Path.Combine(referenceDirectory, fileName + "-export.png"), ImageFormat.Png);
            File.WriteAllText(Path.Combine(referenceDirectory, fileName + "-bounds.json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(new { x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height }));
            int different = 0;
            for (int y = 0; y < bounds.Height; y++)
                for (int x = 0; x < bounds.Width; x++)
                    if (expected.GetPixel(x, y).ToArgb() != rendered.GetPixel(x, y).ToArgb()) different++;
            Smoke.Assert(different == 0, "Avatar primitive recomposition differs: " + clip.name + " time=" + timeMs + " pixels=" + different);
            Console.WriteLine($"AVATAR_PIXELS {clip.name} t={timeMs} {bounds.Width}x{bounds.Height} identical");
        }
        finally
        {
            foreach (var item in actual) item.image.Dispose();
            foreach (var bitmap in primitives.Where(primitive => primitive.OwnsBitmap).Select(primitive => primitive.Bitmap).Distinct()) bitmap.Dispose();
        }
    }

    private static Rectangle Union(Rectangle a, Rectangle b) => a.IsEmpty ? b : Rectangle.Union(a, b);

    private static double Duration(int delay) => delay == 0 ? 120 : Math.Abs((long)delay);
    private static int Alpha(int a0, int a1, double progress) => (int)(a0 + (a1 - a0) * progress);

    private static int Select(double[] durations, double time, out double progress)
    {
        time %= durations.Sum();
        for (int index = 0; index < durations.Length; index++)
        {
            if (time < durations[index])
            {
                progress = time / durations[index];
                return index;
            }
            time -= durations[index];
        }
        progress = 0;
        return 0;
    }

    private static void Draw(Graphics graphics, Bitmap bitmap, int x, int y, int alpha)
    {
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0, 255) / 255f });
        graphics.DrawImage(bitmap, new Rectangle(x, y, bitmap.Width, bitmap.Height), 0, 0, bitmap.Width, bitmap.Height,
            GraphicsUnit.Pixel, attributes);
    }
}
