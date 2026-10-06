using System.Text;
using WzComparerR2.Avatar.Export;
using WzComparerR2.AvatarCommon;
using WzComparerR2.OpenAPI;
using WzComparerR2.WzLib;
using WzUnity;

internal static class KmsAvatarExportSmoke
{
    public static void Run()
    {
        var source = new UnpackedAvatarData(44)
        {
            Unpacked = new Dictionary<string, int>
            {
                ["skinID"] = 0, ["face10k"] = 0, ["faceGender"] = 0, ["faceID"] = 0,
                ["hair10k"] = 3, ["hairGender"] = 0, ["hairID"] = 0,
                ["capID"] = 1, ["capGender"] = 0, ["hasCapPrism"] = 1,
                ["capPrismColorType"] = 2, ["capPrismHue"] = 10, ["capPrismSaturation"] = 120, ["capPrismBrightness"] = 130,
                ["capPrismConvertPureBlack"] = 1, ["capPrism2ColorType"] = 0, ["capPrism2Hue"] = 0,
                ["capPrism2Saturation"] = 100, ["capPrism2Brightness"] = 100,
                ["isLongCoat"] = 1, ["coatID"] = 1, ["coatGender"] = 0, ["pantsID"] = 26, ["pantsGender"] = 0,
                ["isCashWeapon"] = 1, ["cashWeaponID"] = 1, ["cashWeaponGender"] = 0,
                ["ringID1"] = 0, ["ringGender1"] = 0, ["ringID2"] = 125, ["ringGender2"] = 6,
                ["ringID3"] = 0, ["ringGender3"] = 0, ["ringID4"] = 0, ["ringGender4"] = 2,
                ["mixHairColor"] = 5, ["mixHairRatio"] = 40, ["mixFaceInfo"] = 325,
                ["earType"] = 2, ["weaponMotionType"] = 3, ["showEffectFlags"] = 1,
                ["customOrigin0"] = 1, ["customOrigin0X"] = 65533, ["customOrigin0Y"] = 7,
                ["customOrigin1"] = 0
            }.Select(item => new DataInfo(item.Key, 16) { Value = item.Value }).ToList()
        };
        source.SetProperties();
        var root = new Wz_Node("Character");
        var nodes = new Dictionary<string, Wz_Node>(StringComparer.OrdinalIgnoreCase) { ["Character"] = root };
        foreach (int id in new[] { 2000, 12000, 20000, 30000, 1000001, 1050001, 1060026, 1700001, 1116125, 1112000 })
        {
            var item = root.Nodes.Add(id.ToString("D8") + ".img");
            var info = item.Nodes.Add("info");
            info.Nodes.Add(new Wz_Node("islot") { Value = id / 10000 == 111 ? "Ri" : "Cp" });
            if (id == 1116125) info.Nodes.Add(new Wz_Node("illusionGrade") { Value = 0 });
            if (id == 1700001) item.Nodes.Add("49");
            nodes["Character/" + item.Text] = item;
        }
        Wz_Node Find(string path) => nodes.TryGetValue(path, out var result) ? result : null;
        var avatar = KmsAvatarExport.CreateAvatar(source, Find);
        Smoke.Assert(avatar.Parts.Length == 29 && avatar.Body.ID == 2000 && avatar.Head.ID == 12000, "KMS skin mapping changed");
        Smoke.Assert(avatar.Ring1 == null && avatar.Ring2.ID == 1116125 && avatar.Ring3 == null && avatar.Ring4.ID == 1112000,
            "KMS empty ring positions were compacted");
        var equipment = UnityAvatarEquipment.Capture(avatar);
        Smoke.Assert(equipment.Single(item => item.slot == "Ring2").isIllusionRing && equipment.All(item => item.slot != "Ring1"),
            "KMS equipment metadata lost grade-zero ring or invented an empty slot");
        Smoke.Assert(avatar.Face.MixColor == 3 && avatar.Face.MixOpacity == 25 && avatar.Hair.MixColor == 5 && avatar.Hair.MixOpacity == 40,
            "KMS original face/hair mix ratios changed");
        var prism = avatar.Cap.PrismData.Get(PrismDataCollection.PrismDataType.Default);
        Smoke.Assert(prism.Type == 2 && prism.Hue == 10 && prism.Saturation == 120 && prism.Brightness == 130 && prism.ConvertPureBlack,
            "KMS prism color values changed");
        Smoke.Assert(avatar.Longcoat.Visible && !avatar.Pants.Visible && avatar.EarType == 2 && avatar.WeaponType == 49
            && avatar.ShowWeaponEffect && !avatar.ShowWeaponJumpEffect && !avatar.EffectVisibles[11], "KMS visibility/weapon flags changed");
        Smoke.Assert(equipment.Single(item => item.slot == "Pants").itemId == "1060026"
            && !equipment.Single(item => item.slot == "Pants").visible
            && equipment.Single(item => item.slot == "Longcoat").visible,
            "Underlying pants were removed or hid the decoded cash longcoat");
        Smoke.Assert(avatar.CustomOrigin["0"].X == -3 && avatar.CustomOrigin["0"].Y == 7, "KMS signed custom origin changed");
        bool missing = false;
        try { KmsAvatarExport.FindItem(Find, "1009999"); } catch (InvalidDataException) { missing = true; }
        Smoke.Assert(missing, "Missing KMS source item silently received a fallback appearance");
        source.UnknownVer = true;
        bool unknown = false;
        try { KmsAvatarExport.CreateAvatar(source, Find); } catch (InvalidDataException) { unknown = true; }
        Smoke.Assert(unknown, "An unknown KMS appearance format was guessed");
        source.UnknownVer = false;
        source.Unpacked.Single(item => item.Name == "skinID").Value = 100;
        source.SetProperties();
        bool widerSkin = false;
        try { KmsAvatarExport.CreateAvatar(source, Find); } catch (InvalidDataException) { widerSkin = true; }
        Smoke.Assert(widerSkin, "A wider skin code silently mapped to a different body ID");

        using var valid = new MemoryStream(Encoding.UTF8.GetBytes("<configuration><WcR2><nexonOpenAPIKey value=\"fixture-secret\" /></WcR2></configuration>"));
        Smoke.Assert(KmsAvatarExport.ReadApiKey(valid) == "fixture-secret", "CLI settings key path changed");
        using var broken = new MemoryStream(Encoding.UTF8.GetBytes("<configuration><WcR2><nexonOpenAPIKey value=\"fixture-secret\""));
        bool malformed = false;
        try { KmsAvatarExport.ReadApiKey(broken); }
        catch (InvalidDataException error)
        {
            malformed = true;
            Smoke.Assert(!error.ToString().Contains("fixture-secret"), "Settings exception leaked key content");
        }
        Smoke.Assert(malformed, "Malformed settings accepted");
        var faceTrack = new WzAnimationTrack
        {
            kind = "face", frames = new() { new WzSpriteFrame { assetId = "open" }, new WzSpriteFrame { assetId = "fixed" } },
            metadata = new() { new WzMetadata { key = "pose/0/0/source", value = "original/default/face" },
                new WzMetadata { key = "pose/0/1/source", value = "original/blink/1/face" },
                new WzMetadata { key = "pose/1/1/source", value = "original/blink/1/face" } },
            poses = new() { new WzTrackPose { poseFrame = 0, frameIndex = 1, assetId = "fixed0" },
                new WzTrackPose { poseFrame = 1, frameIndex = 1, assetId = "fixed1" }, new WzTrackPose { poseFrame = 1, frameIndex = 0, assetId = "open1" } }
        };
        KmsAvatarExport.FreezeFace(new WzAnimationClip { tracks = new() { faceTrack } }, 1);
        Smoke.Assert(faceTrack.frames.Single().assetId == "fixed" && faceTrack.poses.Count == 2
            && faceTrack.poses.All(pose => pose.frameIndex == 0), "Fixed emotion lost its per-body-frame face poses");
        Smoke.Assert(faceTrack.metadata.Single(item => item.key == "pose/0/0/source").value == "original/blink/1/face"
            && faceTrack.metadata.Single(item => item.key == "pose/1/0/source").value == "original/blink/1/face"
            && faceTrack.metadata.All(item => item.key != "pose/0/1/source"), "Fixed emotion relabeled a closed image as the original open frame");
        Console.WriteLine("KMS_APPEARANCE_PASS ringSlots=mapped mix/prism/visibility/weapon/origin=preserved secrets=redacted unknownSource=rejected");
    }
}
