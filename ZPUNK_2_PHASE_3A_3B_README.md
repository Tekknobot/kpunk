# ZPunk 2.0 - Phase 3A/3B: Arrangement + Clip Editing

Baseline: Archive(20261015-124656).zip (the working Phase 2 capture-recovery project).

## Installation

Extract the code patch at the Unity project root, merging the `Assets` folder. Keep all `.meta` files, especially the new DAW component and editor-check scripts. Do not delete existing assets or reset your scene. If you prefer, use the complete patched project ZIP instead.

## New UI

Open ZPunk and select the **ARRANGE** tab in the existing tab row.

- Timeline ruler, five track lanes, horizontal scroll, and zoom (`<`, `>`)
- Select a track by clicking its name
- `+ CLIP` adds a blank one-bar clip after the selected clip (at the next free slot)
- Double-click empty space in a track lane to create a blank clip at that position
- Select a clip, drag its body to move it horizontally; drag near the right edge to resize it
- `SNAP` cycles bar / quarter note / sixteenth note
- `DUPLICATE` makes a **fully independent** copy of the chosen clip and its note/sample content, in the next available position
- `DELETE` removes only the selected clip and unreferenced content
- `LOOP CLIP` changes the selected clip's repeat flag; stretching a clip beyond its source automatically turns it on
- Track `M` and `S` toggle mute and solo on the song document
- `LOOP SONG` and the BPM field control DAW arrangement transport
- `SAVE SONG` persists the edited arrangement in the existing project file
- `DAW: ON/OFF` toggles the Phase 2 song transport (also available near Play)
- The green vertical line is the song playhead while DAW audio is running

IMPORTANT: Blank clips are silent until they receive note/drum/chop events. In this first visual phase, duplicate *existing populated clips* to build an audible arrangement. A free MIDI editor and drum clip editor come in Phase 3C. The dedicated bass Audio Helm instance is also not yet available.

## Expected playback and limitations

- Song event scheduling reuses Phase 2's independent clip planner; it does **not** automatically pick up edits during playback. STOP then PLAY after changes.
- Only the selected track's horizontal timeline is edited when moving clips. Cross-track clip drag is not part of 3A/3B.
- Clips on the same track may overlap if manually moved; overlapping events can layer. Duplication avoids initially stacking clips over occupied positions.
- Imported legacy patterns are left unchanged; the first DAW edit deep-copies the current song preview into an authored arrangement.
- An authored arrangement with no playable events is intentionally silent; it never resurrects the old step-grid beat.
- Save/load preserves the authored arrangement even when you are no longer in DAW mode.
- No audio export, plugin automation, or new piano-roll editor is included in this phase.

## Unity verification

1. Open the project in Unity 2022.3.62f2 (or your current compatible Unity editor), wait for script compilation.
2. Run **ZPunk > 2.0 > Run Phase 3 Arrangement Checks**. This tests deep duplicate independence, left-drag offset, move/resize timing, clip looping, JSON roundtrip and linked-content deletion.
3. Program a four-on-the-floor drum beat on the old drums page, click ARRANGE. The Drums track should display its imported one-bar clip.
4. Select it and press DUPLICATE twice. Press DAW ON and Play. Hits should continue through the duplicated bars. Stop, drag the last clip to a later bar, Play again, and listen for the new gap.
5. Press SAVE SONG, open another project, and reload the original one. Confirm clip positions and track flags return.
6. Verify the existing DRUMS, KEYS, SYNTH and PAD pages still work in legacy (DAW OFF) mode.

The archive was statically inspected and structurally validated, but Unity itself is not available in the patching environment. Actual compilation, drag gestures, and audio playback need to be verified in the Unity Editor.

## Updated files

- Assets/UI Toolkit/UnityThemes/Main.uxml
- Assets/Scripts/KMusicTabsUI.cs
- Assets/Scripts/KMusicProjectManager.cs
- Assets/Scripts/DAW/SongData.cs
- Assets/Scripts/DAW/ArrangementEditing.cs (+.meta)
- Assets/Scripts/DAW/ZPunkArrangementUI.cs (+.meta)
- Assets/Editor/ZPunkDAWPhase3Checks.cs (+.meta)

## Phase 2 compatibility guard

`SongData.arrangementAuthored` is an optional serialized boolean. Old project files without it still deserialize at schema version 2. An empty older non-authored native snapshot will continue falling back to the live grid; a deliberately emptied Phase 3 arrangement will remain empty.
