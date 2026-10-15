# ZPunk 2.0 — Phase 2 empty-song playback hotfix

This hotfix addresses the warning:

`[ZPunk DAW] This arrangement has no playable note/drum events.`

## What changed

- If the legacy pattern chain is enabled but resolves to **zero playable events**, the project manager now previews the **currently visible/edited 16-step pattern** (provided it contains events). The saved bank and project are not modified.
- When the chain is disabled, the live grids remain the direct source for DAW preview.
- If both snapshots are truly empty, the Console now reports `LIVE CAPTURE EMPTY` with counts for drum hits, visible lit drum pads, synth steps, pad steps, chop steps, and runtime component references. These counts help isolate a UI-to-pattern capture problem.
- The DAW transport refuses to start a silent empty plan rather than setting `IsPlaying=true`.
- The Phase 2 Editor checks cover a blank chain alongside an active live beat.

## Installation

In a project that already has the Phase 2 live-playback patch:
1. Back up the project first (or commit it).
2. Exit Play Mode in Unity.
3. Extract this archive into the **project root**, merging `Assets/` and replacing the matching `.cs` files.
4. Let Unity recompile.
5. Program kick notes at steps 1, 5, 9, and 13, switch DAW ON, press PLAY.
6. Optionally run `ZPunk > 2.0 > Run Phase 2 Arrangement Checks`.

## If it is still silent

Copy the entire new `[ZPunk DAW] LIVE CAPTURE EMPTY:` line from Console. In particular:
- `visibleDrumPads > 0, drumHits = 0` indicates the UI is displaying notes that were not captured in the sequencer mask.
- `visibleDrumPads = 0, drumHits = 0` means no current drum hits were captured; check you're editing the same drum grid.
- `drumHits > 0` with no planned events means the converter/planner needs further investigation.

## Scope

This is a source-level hotfix only. The archives were statically inspected; Unity compilation and device/audio playback cannot be run in this environment. No mixer, audio plugin, scenes, kits, or saved-project files are included or modified.
