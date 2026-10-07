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
`Character/_Canvas/00002000.img/dead/0/body`: a 28×28 ghost **body**, origin
(13,27), with the native 120 ms default delay. There is no `die` action. The
authored `face=1` and body `map/neck=(1,-28)` connect the equipped front head,
default face, mixed hair and cap through the normal AvatarCanvas bone renderer.
For 깽쿤 the head bone is `(1,-43)` and its brow bone is `(-3,-48)`. Omitting these
attachments produced the headless ghost and has been corrected.

The non-looping `dead` preserves the original ghost body, equipped head prism,
hair mix, cap and original cap effect. Native-render body SHA-256:
`8c4e42dead80234a5fa0084085c8c4c04f1da0e319652fd1fa2014ff13397d5b`.
The earlier raw body-only PNG remains preserved as an existing asset, but does
not replace the normal equipped death appearance. Correct an existing 15-action
bundle with `-DeathOnly`; it replaces only `dead` at its existing index and
preserves all other 14 actions, PNGs, outfit metadata and face-map bytes. A native
reference preview is written beside the merged bundle in
`previews/dead-with-equipped-head.png`.

`-DeathCharacterNames` explicitly selects native human outfits that need this
equipped death action; its default remains `@('깽쿤')`. A selected illusion-ring
appearance is rejected rather than replacing its complete native sprite with
human head/body layers. Verification accepts the same explicit list and checks
the selected outfit's actual body/head source metadata. Native default/invalid
prism values leave raw pixels untouched, as AvatarCanvas does.

## Event-only guest 만사기찬 (2026-10-07)

The saved KMS settings were used for one lookup of the exact name `만사기찬`;
no key or response headers are emitted. The character has 19 equipped parts and
no illusion ring. Body `Character/00002016.img`, head `Character/00012016.img`,
face `Character/Face/00051120.img`, hair `Character/Hair/00048520.img`, and cap
`Character/Cap/01007232.img` retain native attachment, colour and effect options.

Reproduce the source-only guest export in fresh directories:

```powershell
./Build/Query-KmsNativeAppearanceBatch.ps1 `
  -CharacterNames @('만사기찬') -OutputDirectory 'D:/fresh/guest-lookup'
./Build/Export-KmsNativeAvatarBatch.ps1 -AppearanceCacheDirectory 'D:/fresh/guest-lookup'
./Build/Export-MapleLiveTravelMotions.ps1 `
  -NativeBundleDirectory @('D:/fresh/guest-lookup/01-만사기찬/avatar-327d407a39022f981468e53b') `
  -DeathCharacterNames @('만사기찬') -OutputDirectory 'D:/fresh/guest-motions'
./Build/Verify-MapleLiveTravelMotions.ps1 `
  -ResultFile @('D:/fresh/guest-motions/travel-motion-results.json') `
  -DeathCharacterNames @('만사기찬') -VerificationFile 'D:/fresh/guest-verification.json'
```

The 15-action result includes stand1/stand2/sit/prone, their blink variants,
walk1/walk2/jump and their blink variants, and native non-looping equipped-head
`dead`. Its 10 death tracks include original ghost body, head, face, hair and cap;
it does not omit the character's head or invent a separate death animation.
Source verification passed 748 checks, all 95 PNGs, and exact original body/head
RGBA comparisons. Import only the returned merged bundle as an initially hidden
event guest. It must not be registered in the regular chat-user bindings or the
normal 30-character actor list.

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
| 깽쿤 | append walk1, walk2, jump, matching blink variants and ghost dead with equipped head |
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
hash, source-node availability and original body clocks. Complete ring sprites
are compared pixel-for-pixel in RGBA against resolved WZ canvases, including
dimensions, original origins and action delays. Death checks require the native
neck→head→brow attachments, original positioned head/hair/cap/face, native 120 ms
clock and exact body/head RGBA after the equipped prism. The original travel
export passed 24,711 checks across 27 prepared native actors and 3,118 PNG checks;
the subsequent death-head correction passed 594 checks, preserved all 14 other
actions and 131 PNGs, and compared both body/head prism results in RGBA. This is
source verification. The complete 27-actor source set with this correction
passed 24,358 checks, 3,119 PNG checks and 16 original RGBA frame comparisons.
Unity import, runtime preview and project tests belong to the MapleLive integration.
