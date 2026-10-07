# MapleLive original travel motions

`Export-MapleLiveTravelMotions.ps1` appends source motions to existing native
outfit bundles. It uses the already compiled reader in
`Tests/UnityExportSmoke/bin/Release/net8.0-windows`, original `Base.wz`, and the
previously exported `parts/*` metadata. It does not query character APIs, read API
settings, build applications, or start/import into Unity.

The restored outfit must reproduce the existing `AppearanceId` exactly. This
includes visible slots, mixed hair/face colours, both prism channels, custom
origins, cap masks and rendering flags. A merged bundle preserves its existing
bundle/entity IDs, default action, equipment inventory, all existing clips and
face metadata. Old PNGs and `face-variant-map.json` remain unchanged.

## Export and application

Pass original bundle directories and a fresh destination directory:

```powershell
./Build/Export-MapleLiveTravelMotions.ps1 `
  -NativeBundleDirectory @('D:/path/to/existing/avatar-ID') `
  -OutputDirectory 'D:/path/to/fresh/travel-motions'
```

Import each returned merged `output` directory with
`WzUnityImporter.ImportDirectory(output, false)`. Keep the original import owner,
wrapper prefab and animation set GUIDs. Existing stand/sit/prone clips are
preserved; the normal importer can update the set that it already owns.

For a native set moved to a custom Unity folder without its original bundle,
read only the stored animation set's exported plain scalar metadata:

```powershell
./Build/Export-MapleLiveTravelMotions.ps1 `
  -StoredAnimationSetPath 'D:/path/to/existing/Animations/avatar-ID.asset' `
  -StoredCharacterName '묵중' -StoredActions @('walk2') `
  -OutputDirectory 'D:/path/to/fresh/mook-motion'
```

**This second output is a motion-only source bundle. Append `walk2` to the existing
custom-location animation set. Do not replace it with this one-action set or
rebind the actor.** The existing 묵중 outfit uses emotion `blink`; that authored
emotion is preserved. Its original `walk1`, `jump`, `fly` and blink profile remain
with the existing set.

## Original actions

Normal body `walk1` and `walk2` contain four 180 ms frames, and `jump` contains one
200 ms frame. The matching equipped body/head/gear WZ nodes determine each
character's layers. Added `_blink` clips preserve independent original face
clocks and body-dependent attachment poses.

The verified illusion ring is `Character/Ring/01116126.img`. Its locomotion is
`move`, with six 150 ms complete sprites; `jump` is one complete sprite with the
native 120 ms default delay. There are no `walk1` or `walk2` ring actions, so no
human walking layers or aliases are invented.

깽쿤's original body is `Character/00002042.img`. Its `dead/0/body` resolves to
`Character/_Canvas/00002000.img/dead/0/body`: a complete 28×28 ghost, origin
(13,27), with the native 120 ms default delay. There is no `die` action. The
exported non-looping `dead` consists of that original PNG alone, without the
ordinary AvatarCanvas default head/hair/face/cap fallbacks. SHA-256:
`ae8909e9b2bee3af27cab3c276a8c0124f6cc3575033e0fd567ab1259c2b9d18`.

## Current 30-character inventory (2026-10-07)

| Character | Original motions prepared or already present |
|---|---|
| DevenPD | append walk1, walk2, jump and matching blink variants |
| Maiden | append walk1, walk2, jump and matching blink variants |
| SoulSilver | append walk1, walk2, jump and matching blink variants |
| WindKit | append walk1, walk2, jump and matching blink variants |
| 달슬우 | append walk1, walk2, jump and matching blink variants |
| 돔팡 | append walk1, walk2, jump and matching blink variants |
| 두부 | append walk1, walk2, jump and matching blink variants |
| 둥글동글썬콜 | append walk1, walk2, jump and matching blink variants |
| 만득 | append walk1, walk2, jump and matching blink variants |
| 무드컷 | append walk1, walk2, jump and matching blink variants |
| 뭐이딴프리렌 | append walk1, walk2, jump and matching blink variants |
| 비정상인 | append walk1, walk2, jump and matching blink variants |
| 시툰바람지기 | append walk1, walk2, jump and matching blink variants |
| 쏠트앤패파 | append walk1, walk2, jump and matching blink variants |
| 엔꼬짱 | append walk1, walk2, jump and matching blink variants |
| 인싸유생 | append walk1, walk2, jump and matching blink variants |
| 저능아님 | append walk1, walk2, jump and matching blink variants |
| 텔미리 | append walk1, walk2, jump and matching blink variants |
| 트롬본씨 | append walk1, walk2, jump and matching blink variants |
| 퐁퐁미래 | append walk1, walk2, jump and matching blink variants |
| 메르세랍니다 | append walk1, walk2, jump and matching blink variants |
| 코루살껄 | append walk1, walk2, jump and matching blink variants |
| 란팡 | append walk1, walk2, jump and matching blink variants |
| 깽쿤 | append walk1, walk2, jump, matching blink variants and original ghost dead |
| 함께하는광이 | append original ring move and jump; no native ring walk1/walk2 |
| 짱레테짱 | append original ring move and jump; no native ring walk1/walk2 |
| 묵중 | append original walk2 only; walk1, jump and fly already present |
| CheesyCheeto | existing walk1, walk2 and jump retained |
| 神滅星影 | existing walk1, jump and their blink variants retained |
| 엽도리v | existing move and jump retained |

The last three sets are legacy composite exports with `sourceFolder` metadata,
not equipped WZ item inventories. Their existing original-looking motions are
usable. Reconstructing additional layers would require new appearance data, so
their current appearance and animation sets are retained.

## Verification

Run the read-only source checks against each generated `travel-motion-results.json`:

```powershell
./Build/Verify-MapleLiveTravelMotions.ps1 `
  -ResultFile @('D:/path/to/travel-motion-results.json') `
  -VerificationFile 'D:/path/to/verification.json'
```

It verifies existing clip/metadata/inventory digests and face-map bytes, every PNG
hash, source-node availability and original body clocks. Complete ring and ghost
sprites are compared pixel-for-pixel in RGBA against resolved WZ canvases,
including dimensions, original origins and action delays. The 2026-10-07 export
passed 24,711 checks across 27 prepared native actors, 3,118 PNG checks and all
15 original ring/ghost frame comparisons. This is source verification; Unity
import, runtime preview and project tests belong to the MapleLive integration.
