# ZPunk 2.0 — DAW usability and instrument routing update

This project is based on the archive uploaded 2026-10-15 16:49:58. Replace the entire Unity project with this extracted folder; keep your previous project as a backup.

## Interface changes

- Hamburger menu expands downward within the header, with New, Open, Save, and current project information.
- BPM text in the transport is a readout, not an editable field. The BPM slider lives in Arrange and updates song tempo (applies to playback on restart).
- Project modal has smaller, squared controls, reduced padding, and a mobile-friendly scrollable list.
- Bottom Arrange / Create / Sound navigation remains inside the workspace with extra height and padding.
- Create's instrument selector and action strips never shrink individual buttons. They scroll horizontally as needed.
- Dragging empty note space does not paint notes. New notes are entered on tap release; existing MIDI notes move/resize with a drag.
- Create now renders a clip-local playhead based on the shared DAW transport tick.
- Full MIDI pitch range 0–127 is accessible using the octave controls (two-octave editing viewport).
- Chops now shows 16 lanes, matching the existing sample chopping limit.
- Drum/chop row labels and grid are sized from the same fixed row height and ruler height.

## Synth tracks

- Sound has Bass / Synth / Pads selectors with independent controller routing and preset controls.
- New Bass Synth prefab + mixer + runtime rack target Audio Helm native channel 2. Main Synth and Pads retain their existing routing and presets.
- Sound macro sliders use actual Audio Helm parameters. Main Synth and Pads write through their existing parameter buses when available; Bass writes its controller directly.
- The legacy synth surfaces and original sound assets are preserved for compatibility.

## Verification in Unity

The project ZIP and UXML can be verified statically here, but Unity runtime compilation, Audio Helm native processing, and actual device layout must be checked in Unity 2022.3.62f2. Start with these checks:

1. Run the project in the same iPhone simulator profile as your screenshots. Hamburger should reveal New / Open / Save and slide closed without hiding the timeline.
2. Arrange: move BPM slider and verify the readout updates. Restart Play after changing BPM.
3. Create: tap to place a drum/chop note, then swipe the grid. Swiping must not add notes. Watch the playhead move while an active clip plays.
4. Create: use Oct +/- until the MIDI pitches reach low C and high G; select Chops and verify lanes S1–S16.
5. Sound: select Synth, Pads, and Bass; change the preset on each and audition a one-bar MIDI clip. Verify all three are audible independently and parameters affect only the selected instrument.
6. Open Projects, rename/load/save; check the card stays within the safe area.

Known intentional restriction: note edits are heard after Stop / Play, not mid-transport. Audio Helm MIDI note dispatch remains frame-timed; drums and samples are DSP scheduled.
