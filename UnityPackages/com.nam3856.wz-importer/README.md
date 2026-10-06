# WZ Unity Importer

Imports the version 1 `wz-unity.json` + PNG bundles produced by WzComparerR2. Unity 6 is required. The package depends on Unity's Newtonsoft JSON package so optional JSON objects retain their `null` meaning.

## Import

1. Add this directory as a local package in Unity Package Manager. MapleLike already references it through `Packages/manifest.json`.
2. Use **Tools > WZ Importer > Import Export Folder...** and select the folder containing `wz-unity.json`.
3. Find generated assets in `Assets/MapleImported/<bundle-id>_<stable-hash>`. Character, monster, NPC and reactor exports create animation data and prefabs. Map exports also create a scene under `Scenes`.

Images use PPU 100 by default (the manifest can specify another positive value), Point filtering, Full Rect sprite meshes, no mipmaps or compression, and the URP 2D Sprite Unlit material. WZ `(x,y)` coordinates become Unity `(x/PPU,-y/PPU)`. Source origins remain independent of sprite pivots, including origins outside the PNG bounds.

Map scenes contain background/tile/object/foreground groups, source foothold edge colliders, one-way platform effectors for floor segments, ladder/rope triggers, portal metadata and source life placements. Placement instances retain their prefab link. Unsupported visuals retain named metadata placeholders and the export warnings. The scene includes an orthographic camera. Gameplay controllers, enemy AI, portal transitions and runtime equipment editing are intentionally separate from importing.

## Playback

```csharp
using WzComparerR2.Unity;

var animation = GetComponent<WzSpriteAnimator>();
animation.Play("walk1");              // false if the action does not exist
animation.Speed = 1;
animation.FlipX = false;
animation.UseAuthoredLoop();           // death actions stop; idle/walk repeat
animation.Completed += action => Debug.Log(action + " completed");
animation.Stop();                      // retains the currently displayed pose
```

`Loop = true/false` overrides the authored loop policy until `UseAuthoredLoop()` is called. `completed` is also available as an Inspector UnityEvent. `PlayTrack(trackId, action)` switches one compatible track without resetting other clocks. `Sample(action, milliseconds)` provides deterministic pose sampling for previews and validation; call `Stop()` if the sampled pose should remain frozen during Play Mode. `startingAction` selects a serialized initial action.

`WzAnimationSet` retains source IDs, paths and metadata. Each action stores independently timed body/face/effect tracks. Original millisecond delays, alpha interpolation, pose-dependent sprite changes, part visibility, origin offsets and per-frame Z/order remain in the data. Slot groups can contain several render fragments. Changing body actions keeps the root anchor stable.

Three-frame body tracks in `stand1`, `stand2`, `stand3`, and `fly` play in the source-frame order `1, 2, 3, 2`, using the original delay each time a frame appears. Playback and `Sample` use the same traversal, including the body pose of native `_blink` actions. Face and item-effect clocks stay independent. Legacy three-frame merged idle tracks also use this order; merged blink timelines and exports that already contain a longer pose sequence keep their authored order.

`WzRandomBlink` on the same object as `WzSpriteAnimator` adds an independent eye clock. Assign a `WzBlinkProfile` mapping each source sprite to matching open, half-closed and closed variants with identical canvas, pivot and pixels-per-unit. The default is a random 5–15 second wait after each burst, followed by 1–2 blinks with a 0.12 second gap. One blink lasts approximately 0.117 seconds: 33.3 ms half-closed, 50 ms closed, and 33.3 ms half-closed again. Open-eye mappings also remove baked blinks from the original timeline. Unmapped sprites keep their original appearance; body and effect clocks continue during every eye phase. Use `BlinkNow(1)` or `BlinkNow(2)` for an immediate blink, and `ResetSchedule(seed)` for reproducible timing. Timing follows scaled game time and pauses at `Time.timeScale = 0`.

The map runtime compacts source `(containerOrder, layerZ + frameZ, sourceOrder, drawOrder)` into Unity sorting orders each frame. Background repeats follow the camera with source bounds, scroll distances and parallax rates. Movement channels support source sine/cosine cycles, constant values, and linear/held keys.

`WzMapCharacterSorting` lets an independently placed character join that map order as one `SortingGroup` on the Default sorting layer. It locates the nearest nonvertical foothold below the character and inserts the group after that source map layer's tiles and footholds, before higher layers and foregrounds. Disable `automaticFootholdLayer` to choose `mapLayer` (0–7) explicitly; set `map` when a scene contains multiple maps. It preserves the animator's internal body/effect ordering. Map sorting also runs in Edit Mode using the saved pose without starting animation clocks. Keep always-visible nameplates in a separate group.

## Reimport and failures

The importer validates schema, asset checksums/dimensions, references, timings, poses, coordinates and relative file paths before creating assets. Identical PNGs in a bundle share one imported texture. Existing data/prefab/material/scene paths retain GUIDs on reimport. The `wz-import-owner.json` file records exactly which paths the importer owns; collisions with unowned files are rejected. Unrelated files are preserved.

Import runs with a snapshot under `Library/WzImportTransactions`. Exceptions or cancellation restore existing file bytes and metadata, removing partial output. Save edits to a generated scene before reimporting it; clean loaded generated scenes are closed and reopened. A successful run writes `import-result.json` with `complete` or `complete_with_warnings`. The copied `wz-unity.json` includes source warning paths and reasons. Changes made inside generated assets are replaced on reimport; add game logic to wrapper prefabs or separate scenes.

## Automation and validation

Editor API: `WzComparerR2.Unity.Editor.WzUnityImporter.ImportDirectory(exportFolder, interactive: false)` returns `WzImportResult`. Run it on the Editor main thread outside Play Mode. For large maps through a tool with a short request timeout, schedule it using `EditorApplication.delayCall` and inspect the resulting `import-result.json` after the Editor finishes.

**Tools > WZ Importer > Validate Importer** runs the checked-in synthetic regression suite in `Tests/Editor`. The callable entry point is `WzComparerR2.Unity.Editor.WzImporterValidation.ValidateSynthetic()`. It checks reimport GUIDs, unrelated-file preservation, scene persistence and prefab links, independent clocks and absolute pose overrides, renderer reuse, non-looping completion, motion timing, rollback and invalid-input preservation. Generated test assets are removed afterward; the report remains at `Library/WzImporterValidation/latest.json`.

Rendering limitations are reported by the exporter, including mixed Spine blend modes and source-specific shaders. Baking those effects to PNG cannot reproduce every engine blend operation. The importer does not attempt to reconstruct gameplay or undocumented shader effects.
