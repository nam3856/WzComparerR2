using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Formatting = Newtonsoft.Json.Formatting;
using WzComparerR2.Avatar.Export;
using WzComparerR2.AvatarCommon;
using WzComparerR2.CharaSim;
using WzComparerR2.Common;
using WzComparerR2.OpenAPI;
using WzComparerR2.UnityExport;
using WzComparerR2.WzLib;
using WzUnity;

/// <summary>Headless equivalent of AvatarForm's KMS appearance-based import.</summary>
internal static class KmsAvatarExport
{
    public static void Run(string basePath, string characterName, string settingsPath, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(characterName)) throw new InvalidDataException("캐릭터 이름이 필요합니다.");
        UnpackedAvatarData appearance = QueryAppearance(characterName.Trim(), settingsPath);
        RequireKnownAppearance(appearance);
        Console.WriteLine("KMS_APPEARANCE name=" + characterName.Trim() + " version=" + appearance.Version);
        using var data = new DataSource(basePath);
        var avatar = CreateAvatar(appearance, data.Find);
        Smoke.Assert(avatar.LoadZ(data.Find("Base/zmap.img")) && avatar.LoadActions(), "원본 캐릭터 동작/zmap을 읽지 못했습니다.");
        avatar.LoadEmotions();
        avatar.LoadAllEffects();
        var strings = new StringLinker();
        strings.Update(data.Find("String"), null, null, null, null);
        string output = ExportVariants(avatar, strings, characterName.Trim(), appearance, outputRoot);
        Console.WriteLine("KMS_AVATAR_DONE=" + output);
    }

    private static UnpackedAvatarData QueryAppearance(string name, string settingsPath)
    {
        // Do not return a config parser / HTTP exception to Smoke.Main, which prints
        // exceptions. Neither the key, headers nor XML content belongs in diagnostics.
        try
        {
            string key;
            using (var stream = File.OpenRead(settingsPath)) key = ReadApiKey(stream);
            var api = new NexonOpenAPI(key, "KMS");
            string ocid = api.GetCharacterOCID(name).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(ocid)) throw new InvalidDataException();
            return api.GetAvatarResult(ocid).GetAwaiter().GetResult();
        }
        catch
        {
            throw new InvalidDataException("KMS 캐릭터 조회를 완료하지 못했습니다. 설정 파일의 키, API 권한과 캐릭터 이름을 확인하세요.");
        }
    }

    internal static string ReadApiKey(Stream stream)
    {
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false
            });
            var document = XDocument.Load(reader);
            string key = document.Root?.Element("WcR2")?.Element("nexonOpenAPIKey")?.Attribute("value")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException();
            return key;
        }
        catch
        {
            throw new InvalidDataException("설정 파일에서 KMS API 키를 읽지 못했습니다.");
        }
    }

    internal static void RequireKnownAppearance(UnpackedAvatarData source)
    {
        if (source == null) throw new InvalidDataException("KMS 외형 정보가 없습니다.");
        if (source.UnknownVer) throw new InvalidDataException("지원하지 않는 KMS 외형 코드 버전입니다: " + source.Version);
        // AvatarForm prefixes the two-digit skin code with 20/120. Do not map
        // an unsupported wider code onto a different original body or face.
        if (!int.TryParse(source.Skin, NumberStyles.None, CultureInfo.InvariantCulture, out int skin) || skin < 0 || skin > 99)
            throw new InvalidDataException("KMS 피부 ID를 확인할 수 없습니다.");
    }

    internal static AvatarCanvas CreateAvatar(UnpackedAvatarData source, Func<string, Wz_Node> find)
    {
        RequireKnownAppearance(source);
        int skin = int.Parse(source.Skin, CultureInfo.InvariantCulture);
        var avatar = new AvatarCanvas
        {
            EmotionName = "default", ActionName = source.WeaponMotionType == 2 ? "stand2" : "stand1",
            EarType = source.EarType, ShowWeaponEffect = source.ShowWeaponEffect,
            ShowWeaponJumpEffect = source.ShowWeaponJumpEffect
        };
        Add(avatar, find, (2000 + skin).ToString(CultureInfo.InvariantCulture), required: true);
        ApplyPrisms(Add(avatar, find, (12000 + skin).ToString(CultureInfo.InvariantCulture), required: true), source.SkinPrismInfo);
        ApplyMix(Add(avatar, find, source.Face, required: true), source.MixFaceColor, source.MixFaceRatio);
        ApplyMix(Add(avatar, find, source.Hair, required: true), source.MixHairColor, source.MixHairRatio);
        foreach (var entry in new[]
        {
            (source.Cap, source.CapPrismInfo), (source.FaceAcc, source.FaceAccPrismInfo),
            (source.EyeAcc, source.EyeAccPrismInfo), (source.EarAcc, source.EarAccPrismInfo),
            (source.Coat, source.CoatPrismInfo), (source.Pants, source.PantsPrismInfo),
            (source.Shoes, source.ShoesPrismInfo), (source.Gloves, source.GlovesPrismInfo),
            (source.Cape, source.CapePrismInfo), (source.Shield, source.ShieldPrismInfo),
            (source.Weapon, source.WeaponPrismInfo), (source.CashWeapon, source.WeaponPrismInfo)
        }) ApplyPrisms(Add(avatar, find, entry.Item1), entry.Item2);

        // The appearance code already chose a coat or longcoat. It may also carry
        // the underlying equipment pants. Those must remain in the inventory,
        // but must not hide a rendered cash longcoat just because they load later.
        if (avatar.Longcoat != null)
        {
            avatar.Longcoat.Visible = true;
            if (avatar.Coat != null) avatar.Coat.Visible = false;
            if (avatar.Pants != null) avatar.Pants.Visible = false;
        }

        // AddPart fills the first free ring position. Use the actual decoded slot
        // instead, so an empty Ring1 never moves Ring2/3/4 or invents an item ID.
        string[] rings = { source.Ring1, source.Ring2, source.Ring3, source.Ring4 };
        for (int slot = 0; slot < rings.Length; slot++)
            if (!string.IsNullOrEmpty(rings[slot])) avatar.SetRing[slot](new AvatarPart(FindItem(find, rings[slot])));
        if (avatar.Cape != null) avatar.Cape.EffectVisible = source.ShowCapeEffect;
        avatar.EffectVisibles[11] = source.ShowCapeEffect;
        for (int index = 0; index < (source.CustomOrigin?.Length ?? 0); index++)
            if (source.CustomOrigin[index].Valid) avatar.CustomOrigin[index.ToString(CultureInfo.InvariantCulture)] = source.CustomOrigin[index].Origin;

        var weaponTypes = avatar.GetCashWeaponTypes();
        if (source.WeaponMotionType == 3 && weaponTypes.Count > 0)
        {
            if (!weaponTypes.Contains(49)) throw new InvalidDataException("원본 캐시 무기에 KMS 건 모션(49)이 없습니다.");
            avatar.WeaponType = 49;
        }
        else avatar.WeaponType = weaponTypes.FirstOrDefault();
        return avatar;
    }

    private static AvatarPart Add(AvatarCanvas avatar, Func<string, Wz_Node> find, string id, bool required = false)
    {
        if (string.IsNullOrEmpty(id))
        {
            if (required) throw new InvalidDataException("KMS 외형에 필수 부위 ID가 없습니다.");
            return null;
        }
        var part = avatar.AddPart(FindItem(find, id));
        if (part == null) throw new InvalidDataException("원본 아이템 info를 읽지 못했습니다: " + id);
        return part;
    }

    internal static Wz_Node FindItem(Func<string, Wz_Node> find, string id)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0)
            throw new InvalidDataException("KMS 아이템 ID가 유효하지 않습니다.");
        string image = value.ToString("D8", CultureInfo.InvariantCulture) + ".img";
        var root = find("Character") ?? throw new InvalidDataException("Character 원본이 없습니다.");
        foreach (var folder in root.Nodes)
        {
            if (folder.Text.Contains("_Canvas", StringComparison.Ordinal)) continue;
            if (folder.Text == image) return find("Character/" + image) ?? throw new InvalidDataException("원본 아이템 추출 실패: " + id);
            if (folder.Nodes[image] != null) return find("Character/" + folder.Text + "/" + image)
                ?? throw new InvalidDataException("원본 아이템 추출 실패: " + id);
        }
        throw new InvalidDataException("현재 Base.wz에 KMS 아이템이 없습니다: " + id);
    }

    private static void ApplyMix(AvatarPart part, string color, string ratio)
    {
        if (!int.TryParse(ratio, NumberStyles.None, CultureInfo.InvariantCulture, out int opacity) || opacity < 0 || opacity > 99
            || !int.TryParse(color, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0 || index > 8)
            throw new InvalidDataException("KMS 혼합색 정보가 유효하지 않습니다.");
        if (opacity > 0) { part.MixColor = index; part.MixOpacity = opacity; }
    }

    private static void ApplyPrisms(AvatarPart part, PrismInfoCollection prisms)
    {
        if (part == null || prisms == null) return;
        var values = new[] { prisms.Prism1, prisms.Prism2 }.Where(value => value?.Valid == true).ToArray();
        if (values.Length == 0) return;
        ApplyPrisms(part, values[0]);
        if (values.Length > 1 && (Gear.IsWeapon(Gear.GetGearType(part.ID.Value)) || Gear.IsCashWeapon(Gear.GetGearType(part.ID.Value))))
            SetPrism(part, values[1], PrismDataCollection.PrismDataType.WeaponEffect);
    }

    private static void ApplyPrisms(AvatarPart part, PrismInfo prism)
    {
        if (part != null && prism?.Valid == true) SetPrism(part, prism, PrismDataCollection.PrismDataType.Default);
    }

    private static void SetPrism(AvatarPart part, PrismInfo prism, PrismDataCollection.PrismDataType kind)
    {
        // AvatarForm.GetPrismCode treats non-positive saturation/brightness as
        // its explicit identity prism; keep that behavior in the headless path.
        bool actual = prism.Brightness > 0 && prism.Saturation > 0 && prism.Hue >= 0;
        part.PrismData.Set(kind, actual ? prism.ColorType : 0, actual ? prism.Hue : 0,
            actual ? prism.Saturation : 100, actual ? prism.Brightness : 100, actual && prism.ConvertPureBlack);
    }

    private static string ExportVariants(AvatarCanvas avatar, StringLinker strings, string name,
        UnpackedAvatarData appearance, string outputRoot)
    {
        string[] actions = { "stand1", "stand2", "sit", "prone" };
        foreach (string action in actions)
            if (avatar.GetActionFrames(action).Length == 0) throw new InvalidDataException("원본 동작이 없습니다: " + action);
        string baseEmotion = "default";
        int? fixedFrame = null;
        if (!string.IsNullOrEmpty(appearance.EmotionFaceAcc))
        {
            string fixedEmotion = WzComparerR2.PluginBase.PluginManager.FindWz("Etc/EmotionFaceAccInfo.img/"
                + appearance.EmotionFaceAcc + "/fixedEmotion").GetValueEx<string>("");
            if (!string.IsNullOrEmpty(fixedEmotion))
            {
                var parts = fixedEmotion.Split('/');
                baseEmotion = parts[0];
                fixedFrame = parts.Length > 1 && int.TryParse(parts[1], out int frame) ? frame : 0;
            }
        }
        if (avatar.GetFaceFrames(baseEmotion).Length == 0) throw new InvalidDataException("원본 기본 표정이 없습니다: " + baseEmotion);
        if (avatar.GetFaceFrames("blink").Length == 0) throw new InvalidDataException("원본 얼굴 blink 프레임이 없습니다.");
        avatar.EmotionName = baseEmotion;
        var primary = new UnityAvatarExporter(avatar, strings);
        string output = Path.GetFullPath(Path.Combine(outputRoot, primary.AppearanceId));
        string variantRoot = Path.Combine(Path.GetFullPath(outputRoot), ".kms-variants-" + Guid.NewGuid().ToString("N"));
        var variants = new List<(string Folder, WzUnityManifest Manifest, string Emotion)>();
        foreach (string emotion in new[] { baseEmotion, "blink" }.Distinct(StringComparer.Ordinal))
        {
            avatar.EmotionName = emotion;
            string folder = Path.Combine(variantRoot, emotion);
            var variant = new UnityAvatarExporter(avatar, strings).Export(folder, actions);
            if (emotion == baseEmotion && fixedFrame.HasValue)
                foreach (var clip in variant.entities.Single().clips) FreezeFace(clip, fixedFrame.Value);
            variants.Add((folder, variant, emotion));
        }
        var entity = variants[0].Manifest.entities.Single();
        entity.displayName = name;
        entity.defaultAction = avatar.ActionName;
        entity.metadata.Add(new WzMetadata { key = "kms/characterName", value = name });
        entity.metadata.Add(new WzMetadata { key = "kms/appearanceVersion", value = appearance.Version.ToString(CultureInfo.InvariantCulture) });
        entity.metadata.Add(new WzMetadata { key = "kms/source", value = "KMS character/basic decoded appearance + original Base.wz" });
        entity.metadata.Add(new WzMetadata { key = "face/blinkSource", value = avatar.Face.Node.FullPathToFile + "/blink" });
        if (fixedFrame.HasValue) entity.metadata.Add(new WzMetadata { key = "face/fixedFrame", value = fixedFrame.Value.ToString(CultureInfo.InvariantCulture) });
        entity.metadata.Add(new WzMetadata { key = "face/availableEmotions", value = string.Join(",", avatar.Emotions.Where(emotion => avatar.GetFaceFrames(emotion).Length > 0)) });
        using var writer = new UnityExportWriter(output, entity.id, "avatar", entity.sourcePath);
        foreach (var variant in variants)
        {
            foreach (var asset in variant.Manifest.assets)
                Smoke.Assert(writer.AddPngFile(Path.Combine(variant.Folder, asset.file)) == asset.id, "Variant PNG hash changed");
            foreach (var warning in variant.Manifest.warnings) writer.Warn(warning.sourcePath, warning.reason);
            if (variant.Emotion != baseEmotion)
                foreach (var clip in variant.Manifest.entities.Single().clips)
                {
                    clip.name += "_" + variant.Emotion;
                    entity.clips.Add(clip);
                }
        }
        writer.Manifest.entities.Add(entity);
        writer.Commit();
        WriteFaceVariantMap(writer.Manifest, output);
        WritePreviews(avatar, baseEmotion, fixedFrame, outputRoot);
        avatar.ClearSkinCache();
        Console.WriteLine("KMS_VARIANTS clips=" + entity.clips.Count + " assets=" + writer.Manifest.assets.Count
            + " equipment=" + entity.equipment.Count + " warnings=" + writer.Manifest.warnings.Count);
        // Keep the original exporter outputs beside the combined bundle as source
        // evidence; only the returned bundle should be imported into Unity.
        return output;
    }

    private static void WriteFaceVariantMap(WzUnityManifest manifest, string output)
    {
        var assets = manifest.assets.ToDictionary(asset => asset.id, StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var clip in manifest.entities.Single().clips)
            foreach (var track in clip.tracks.Where(track => track.kind == "face"))
                foreach (var pose in track.poses.Where(pose => pose.visible && pose.assetId != null))
                {
                    var asset = assets[pose.assetId];
                    rows.Add(new
                    {
                        action = clip.name, trackId = track.id, slot = track.slot,
                        bodyFrameIndex = pose.poseFrame, faceFrameIndex = pose.frameIndex,
                        sourcePath = track.metadata.FirstOrDefault(value => value.key == "pose/" + pose.poseFrame + "/" + pose.frameIndex + "/source")?.value,
                        pose.assetId, file = asset.file, asset.width, asset.height, x = pose.x, y = pose.y,
                        originX = track.frames[pose.frameIndex].originX, originY = track.frames[pose.frameIndex].originY
                    });
                }
        File.WriteAllText(Path.Combine(output, "face-variant-map.json"), JsonConvert.SerializeObject(rows, Formatting.Indented));
    }

    private static void WritePreviews(AvatarCanvas avatar, string baseEmotion, int? fixedFrame, string outputRoot)
    {
        string folder = Path.Combine(Path.GetFullPath(outputRoot), "previews");
        Directory.CreateDirectory(folder);
        var descriptions = new List<object>();
        foreach (string action in new[] { "stand1", "stand2", "sit", "prone" })
        {
            WritePreview(avatar, action, baseEmotion, fixedFrame ?? 0, "default-" + action + ".png", folder, descriptions);
            for (int frame = 0; frame < avatar.GetFaceFrames("blink").Length; frame++)
                WritePreview(avatar, action, "blink", frame, "blink-" + action + "-" + frame + ".png", folder, descriptions);
        }
        File.WriteAllText(Path.Combine(folder, "preview-origins.json"), JsonConvert.SerializeObject(descriptions, Formatting.Indented));
    }

    private static void WritePreview(AvatarCanvas avatar, string action, string emotion, int faceFrame, string file,
        string folder, List<object> descriptions)
    {
        var effects = new ActionFrame[AvatarCanvas.LayerSlotLength];
        for (int slot = 0; slot < effects.Length; slot++) effects[slot] = avatar.GetEffectFrames(action, slot).FirstOrDefault();
        var body = avatar.GetActionFrames(action)[0];
        var face = avatar.GetFaceFrames(emotion)[faceFrame];
        var primitives = avatar.CreateFramePrimitives(avatar.CreateFrame(body, face, null, effects));
        try
        {
            Rectangle bounds = Rectangle.Empty;
            foreach (var primitive in primitives)
            {
                var rectangle = new Rectangle(primitive.Position, primitive.Bitmap.Size);
                bounds = bounds.IsEmpty ? rectangle : Rectangle.Union(bounds, rectangle);
            }
            if (bounds.IsEmpty) throw new InvalidDataException("원본 시각 검증 프레임이 비어 있습니다: " + action);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                foreach (var primitive in primitives)
                    graphics.DrawImageUnscaled(primitive.Bitmap, primitive.Position.X - bounds.X, primitive.Position.Y - bounds.Y);
            bitmap.Save(Path.Combine(folder, file), ImageFormat.Png);
            descriptions.Add(new { file, action, emotion, bodyFrame = 0, faceFrame, width = bitmap.Width, height = bitmap.Height,
                originX = -bounds.X, originY = -bounds.Y, x = bounds.X, y = bounds.Y,
                faceSource = avatar.Face.Node.FullPathToFile + "/" + face.Action + (face.Frame.HasValue ? "/" + face.Frame.Value : "") + "/face" });
        }
        finally
        {
            foreach (var bitmap in primitives.Where(primitive => primitive.OwnsBitmap).Select(primitive => primitive.Bitmap).Distinct()) bitmap.Dispose();
        }
    }

    internal static void FreezeFace(WzAnimationClip clip, int fixedFrame)
    {
        foreach (var track in clip.tracks.Where(track => track.kind == "face"))
        {
            if (fixedFrame < 0 || fixedFrame >= track.frames.Count) throw new InvalidDataException("원본 고정 표정 프레임이 없습니다.");
            var selected = track.frames[fixedFrame];
            track.frames = new List<WzSpriteFrame> { selected };
            track.poses = track.poses.Where(pose => pose.frameIndex == fixedFrame).ToList();
            foreach (var pose in track.poses) pose.frameIndex = 0;
            // Keep the selected original frame's provenance under its new clock
            // index. Otherwise the map would label it with the old frame zero.
            var metadata = new List<WzMetadata>();
            foreach (var item in track.metadata)
            {
                if (!item.key.StartsWith("pose/", StringComparison.Ordinal)) { metadata.Add(item); continue; }
                var path = item.key.Split('/');
                if (path.Length < 4 || path[2] != fixedFrame.ToString(CultureInfo.InvariantCulture)) continue;
                path[2] = "0";
                metadata.Add(new WzMetadata { key = string.Join("/", path), value = item.value });
            }
            track.metadata = metadata;
            track.metadata.Add(new WzMetadata { key = "sourceFixedFrameIndex", value = fixedFrame.ToString(CultureInfo.InvariantCulture) });
        }
    }
}
