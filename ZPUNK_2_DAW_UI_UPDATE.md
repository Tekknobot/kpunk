# ZPunk 2.0 - Clip-Native DAW UI Update

Baseline: user's `Archive 2(1).zip` (October 15, 2026).

## Changes
- Arrange: 5-track timeline, fully visible stacked M/S controls, two compact toolbar rows, EDIT action to open selected clip, clip operations and drag/resize retained.
- Create: brand-new `ZPunkClipEditorUI` directly edits the DAW song clip contents; drum/slice tracks have eight-lane step grids and melodic tracks have a two-octave piano roll, horizontal scrolling, note duration, moving, velocity, deletion, and save.
- Sound: brand-new `ZPunkSoundUI` replaces oversized UI with compact custom sliders for live synth filter, envelope, tone, movement and master parameters. Actual legacy preset Prev/Next/Name elements are reused to preserve Audio Helm preset callbacks.
- Header: Play/Stop always use Phase 2 song mode, BPM editable in the top bar, hamburger menu with NEW/OPEN/SAVE project plus project label. Original project-control references stay live.
- Navigation: only ARRANGE / CREATE / SOUND; project + preset popup overlays retained above the workspace. Legacy sequencing UI is hidden rather than deleted, preserving dependencies and old saves.
- Panel safe area continues to be managed by the project's existing panel/screen setup.

## How to use
1. Open `Assets/Scenes/Main.unity`, enter Play mode and verify no Console compile errors.
2. On ARRANGE, select track, tap + CLIP, and tap EDIT.
3. On CREATE, tap a drum/slice cell to toggle a hit. For a melodic track, tap the piano grid to add notes; drag a note to move it, drag near its right edge to resize, or use LEN +/- and NOTE DEL. Tap SAVE to persist the project.
4. Stop and press Play again to hear modifications; Phase 2 snapshots clips on playback start.
5. On SOUND, swipe through the modules and drag custom horizontal sliders. PREV/NEXT change the live synth preset. The existing full parameter implementation remains bundled for compatibility but is not displayed.
6. Hamburger contains New, Open, Save and current project name.

## Important limitations
- Unity and iPhone Device Simulator could not be run here; final visual fit and compilation must be verified in Unity.
- Bass remains a song data/piano-roll track without a dedicated third Audio Helm controller in this baseline. It can be edited but will not produce independent bass audio until the instrument is provisioned.
- Sample slices require the project's existing sample/slice asset setup; this patch edits slice triggers, not waveforms.
- Live edits are audible only after restarting playback, as agreed for this phase.
- Undo/redo and advanced MIDI tools are not implemented in this release.

## Changed files
- `Assets/Scripts/KMusicTabsUI.cs`
- `Assets/Scripts/DAW/ZPunkArrangementUI.cs`
- `Assets/Scripts/DAW/ZPunkClipEditorUI.cs` (new, with meta)
- `Assets/Scripts/DAW/ZPunkSoundUI.cs` (new, with meta)
