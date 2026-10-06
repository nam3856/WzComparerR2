using Newtonsoft.Json;
using WzComparerR2.Avatar.Export;
using WzComparerR2.AvatarCommon;
using WzComparerR2.Common;
using WzComparerR2.WzLib;
using WzUnity;

internal static class AvatarEquipmentSmoke
{
    public static void RunOriginal(Func<string, Wz_Node> find)
    {
        var avatar = new AvatarCanvas();
        var cases = new[] { (Id: 1112000, Illusion: false), (Id: 1114500, Illusion: true),
            (Id: 1114501, Illusion: true), (Id: 1116125, Illusion: true) };
        foreach (var fixture in cases)
        {
            string path = "Character/Ring/" + fixture.Id.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".img";
            var node = find(path);
            Smoke.Assert(node != null && avatar.AddPart(node) != null, "Missing original ring fixture: " + path);
        }
        var equipment = UnityAvatarEquipment.Capture(avatar);
        Smoke.Assert(equipment.Count == cases.Length, "Original ring inventory lost a non-rendering part");
        foreach (var fixture in cases)
        {
            var item = equipment.Single(entry => entry.itemId == fixture.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Smoke.Assert(item.illusionRingClassificationKnown && item.isIllusionRing == fixture.Illusion,
                "Original WZ ring classification disagreed: " + fixture.Id);
            string grade = item.metadata.FirstOrDefault(value => value.key == "illusionRing/grade")?.value ?? "absent";
            Console.WriteLine("ORIGINAL_RING id=" + item.itemId + " grade=" + grade
                + " illusion=" + item.isIllusionRing + " known=" + item.illusionRingClassificationKnown
                + " image=" + item.hasImage + " evidence=" + item.illusionRingSourcePath);
            if (fixture.Id == 1116125) Smoke.Assert(grade == "0", "Grade-zero original fixture changed");
            if (fixture.Id == 1114500) Smoke.Assert(!item.hasImage, "Info-only original fixture unexpectedly acquired image actions");
        }
        Console.WriteLine("ORIGINAL_EQUIPMENT_PASS rings=4 normal=1 illusion=3 gradeZero=verified");
    }

    public static void Run()
    {
        var avatar = new AvatarCanvas();
        for (int slot = 0; slot < avatar.Parts.Length; slot++)
            avatar.Parts[slot] = new AvatarPart(Item(1000000 + slot, null));
        avatar.Ring1 = new AvatarPart(Item(1114500, 1)) { Visible = false, EffectVisible = false };
        avatar.Ring2 = new AvatarPart(Item(1116125, 0));
        avatar.Ring3 = new AvatarPart(Item(1112000, null));
        avatar.Ring4 = new AvatarPart(Item(1114501, "malformed"));
        var strings = new StringLinker();
        strings.StringEqp[1114500] = new StringResult { Name = "원본 반지 이름", FullPath = "String\\Eqp.img\\Eqp\\Ring\\1114500" };
        var equipment = UnityAvatarEquipment.Capture(avatar, strings);
        Smoke.Assert(equipment.Count == AvatarCanvas.PartLength && equipment.Select(item => item.slotIndex).Distinct().Count() == 29,
            "Equipment capture omitted a configured slot");
        var hidden = equipment.Single(item => item.slot == "Ring1");
        Smoke.Assert(hidden.itemId == "1114500" && !hidden.visible && !hidden.effectVisible && !hidden.hasImage,
            "Invisible info-only ring was omitted or treated as rendered");
        Smoke.Assert(hidden.isIllusionRing && hidden.illusionRingClassificationKnown,
            "Info-only illusion ring must classify independently from prone availability");
        Smoke.Assert(hidden.name == "원본 반지 이름" && hidden.nameSourcePath.Contains("String\\Eqp.img"),
            "Available original item names were lost");
        Smoke.Assert(hidden.metadata.Any(value => value.key == "info/illusionGrade" && value.value == "1")
            && hidden.illusionRingSourcePath.EndsWith("info\\illusionGrade", StringComparison.Ordinal),
            "Original illusionGrade or evidence path was lost");
        var zero = equipment.Single(item => item.slot == "Ring2");
        Smoke.Assert(zero.isIllusionRing && zero.illusionRingClassificationKnown, "Grade zero illusion ring was treated as normal");
        var normal = equipment.Single(item => item.slot == "Ring3");
        Smoke.Assert(!normal.isIllusionRing && normal.illusionRingClassificationKnown, "Missing marker in readable info was treated as illusion");
        var malformed = equipment.Single(item => item.slot == "Ring4");
        Smoke.Assert(!malformed.isIllusionRing && !malformed.illusionRingClassificationKnown, "Malformed marker became a known classification");

        var unknown = new WzEquippedItem();
        UnityAvatarEquipment.ClassifyIllusionRing(null, false, Item(1114500, 1), unknown);
        Smoke.Assert(!unknown.illusionRingClassificationKnown && !unknown.isIllusionRing, "Missing item ID was guessed");
        UnityAvatarEquipment.ClassifyIllusionRing(1114500, false, new Wz_Node("01114500.img"), unknown);
        Smoke.Assert(!unknown.illusionRingClassificationKnown, "Unavailable info was treated as a normal ring");
        UnityAvatarEquipment.ClassifyIllusionRing(1114500, false, Item(1114500, -1), unknown);
        Smoke.Assert(unknown.illusionRingClassificationKnown && !unknown.isIllusionRing, "Negative grade was treated as illusion");
        UnityAvatarEquipment.ClassifyIllusionRing(1114500, true, Item(1114500, 1), unknown);
        Smoke.Assert(!unknown.illusionRingClassificationKnown, "Skill-forced ID was treated as original ring evidence");

        var cyclic = Item(1114500, new Wz_Uol("illusionGrade"));
        UnityAvatarEquipment.ClassifyIllusionRing(1114500, false, cyclic, unknown);
        Smoke.Assert(!unknown.illusionRingClassificationKnown, "Cyclic UOL was treated as readable equipment info");
        var linked = Item(1114500, new Wz_Uol("grade"));
        linked.Nodes["info"].Nodes.Add(new Wz_Node("grade") { Value = 0 });
        UnityAvatarEquipment.ClassifyIllusionRing(1114500, false, linked, unknown);
        Smoke.Assert(unknown.illusionRingClassificationKnown && unknown.isIllusionRing
            && unknown.illusionRingSourcePath.EndsWith("info\\grade", StringComparison.Ordinal), "Linked grade evidence was not resolved");

        var legacy = JsonConvert.DeserializeObject<WzEntity>("{\"id\":\"old-avatar\",\"kind\":\"avatar\"}");
        Smoke.Assert(!legacy.hasEquipmentMetadata && legacy.equipment.Count == 0, "Old export was upgraded to invented outfit information");
        var entity = new WzEntity { hasEquipmentMetadata = true, equipment = equipment };
        var roundTrip = JsonConvert.DeserializeObject<WzEntity>(JsonConvert.SerializeObject(entity));
        Smoke.Assert(roundTrip.hasEquipmentMetadata && roundTrip.equipment.Count == 29
            && roundTrip.equipment.Single(item => item.slot == "Ring2").isIllusionRing, "Equipment manifest round-trip lost inventory/classification");

        var exporter = new UnityAvatarExporter(avatar, strings);
        strings.StringEqp[1114500].Name = "다른 이름";
        Smoke.Assert(new UnityAvatarExporter(avatar, strings).AppearanceId == exporter.AppearanceId,
            "Localized names changed existing appearance identity");
        Console.WriteLine("AVATAR_EQUIPMENT_PASS slots=29 ringVisibility=preserved gradeZero=verified oldExports=unknown");
    }

    private static Wz_Node Item(int id, object grade)
    {
        var node = new Wz_Node(id.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".img");
        var info = node.Nodes.Add("info");
        info.Nodes.Add(new Wz_Node("islot") { Value = id / 10000 == 111 ? "Ri" : "Cp" });
        if (grade != null) info.Nodes.Add(new Wz_Node("illusionGrade") { Value = grade });
        return node;
    }
}
