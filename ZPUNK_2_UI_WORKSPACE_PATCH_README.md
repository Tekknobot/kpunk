# ZPunk 2.0 — Arrangement-first Workspace (Phase 3 UI Fix)

Based on `Archive(20261015-132620).zip`. This patch is intended to replace the original seven-tab layout without modifying the audio engine, song serializer, or Unity scene GUIDs.

## Why ARRANGE was missing

`KMusicTabsUI.cs` searched for `PageArrangement` and `TabArrangement` in `Assets/KMusic/UI/Toolkit/UXML/Main.uxml`, but the active scene's UXML contained neither. The original Phase 3 arrangement component therefore received a `null` parent and rendered nothing. The scene uses the Toolkit UXML with GUID `150278af6f8841e4498fed0b32fbef42`, NOT the similarly named Resources fallback.

## New layout

- **ARRANGE**: default multitrack timeline. Real clips, transport, zoom, mute/solo, snap, loops, project saving.
- **CREATE**: original DRUMS, KEYS, CHORDS, and SAMPLES editors, selected from a horizontal instrument strip. Long-press KEYS / CHORDS still starts their old randomizer.
- **SOUND**: original Audio Helm SYNTH 1 / SYNTH 2 interfaces.
- **CHAIN**: original pattern chain, marked as legacy workflow (not falsely presented as a full mixer).
- Desktop and larger landscape windows: slim left navigation. Portrait and narrow windows: four-target bottom dock. Touch controls are scaled based on actual phone resolution and the Unity PanelSettings reference scale.
- Transport / project controls remain in the same UIDocument with their original names and callbacks. Their compact top bar is **above** the pages, instead of under them.
- Original huge tab strip is hidden. All old named legacy pages remain in the visual tree for existing bindings.
- The source UXML contains `PageArrangement`; `KMusicTabsUI` also supplies a runtime fallback if an older UXML is assigned later.
- Legacy instrument pages now sit inside a vertical/horizontal scrolling viewport.
- Arrangement actions are in two horizontally scrollable rows rather than wrapping into several rows on a phone. `+ CLIP`, COPY, DELETE, < BAR, BAR >, SHORTER, LONGER, LOOP CLIP; zoom, snap, song loop, DAW, BPM, save remain available.
- Clips show miniature event marks for notes / drum hits / chops, not just flat color rectangles.

## Install

**Code patch:** Extract the patch ZIP into the Unity project root, preserving `Assets/` and `.meta` files. The patch includes source scripts, active+fallback UXML, active USS, and a UI validation Editor utility.

**Full project:** Open as an existing Unity project. `Library/` and other generated folders are intentionally excluded; Unity will regenerate them.

## Smoke tests

1. Open `Assets/Scenes/Main.unity`; enter Play Mode. The **ARRANGE** workspace should appear first with 5 track lanes, instead of seven tab buttons.
2. Select CREATE > DRUMS; enter kicks on steps 1, 5, 9, and 13. Enable DAW mode from transport or ARRANGE, stop then play. Existing Phase 2 capture behavior remains in effect.
3. Return to ARRANGE; select a clip and use COPY / < BAR / BAR > / SHORTER / LONGER. Changes take effect on the next Play, not immediately while playing.
4. Switch between CREATE, SOUND and CHAIN. Verify legacy controls and project NEW/LOAD still work.
5. Change Game view to phone portrait. A four-button dock should sit at the bottom, and editing actions should scroll horizontally rather than stack vertically.
6. Use menu **ZPunk > 2.0 > Validate Workspace UI** to confirm that the assigned Toolkit UXML contains the timeline page and all instrument/transport elements.
7. Optionally run the existing **ZPunk > 2.0 > Run Phase 3 Arrangement Checks** for clip/event model regression checks.

## Known limits

- Phase 3C piano-roll note editing is not part of this fix; blank clips still need a dedicated note editor.
- Live edits during running DAW playback still require stopping and playing again.
- The existing bass output limitations from Phase 2 are unchanged; this is a UI-only functional redesign.
- The legacy sound editors were made more compact/scrollable but have not been entirely redrawn, especially on small phones.
- Validation covers XML importability, structure, names, scene asset GUID, package integrity and basic static C# balance. Unity Editor compilation and physical-device UI testing were not possible here.
