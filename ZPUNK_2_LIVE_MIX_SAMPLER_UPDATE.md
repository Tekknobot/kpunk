# ZPunk 2.0 — Live Editing, Mixer and Sampler Update

Based on `Archive(20261015-180253).zip`.

## What's changed

- Arrangement clip edits, MIDI and drum-step edits refresh the playback event plan while Play continues. Musical position is preserved.
- Tempo changes adjust the active DSP timebase without restarting playback or stopping held melodic notes.
- Changing song loop length or toggling song looping updates the active transport.
- Creating, duplicating, moving, or deleting clips renumbers that track's clips sequentially: Drums 1, Drums 2, etc.
- A dedicated **MIX** workspace provides individual track gain/mute/solo and eight drum-channel faders/mutes using the existing ParameterBus and Unity AudioMixer. This is not a decorative mixer.
- Create contains a **SAMPLER** shortcut and a new sample workstation surface with real sample browser, prev/next, play/stop, an existing waveform view, 4/8/16 auto slicing, Apply, and 16 chop audition pads. Audio is powered by the existing KMusicPlayerUI and KMusicDrumSequencer.
- Pads preset controls now use the existing cycling logic and wrap correctly at both ends of the loaded preset list.
- Create displays a DAW playhead against the currently edited clip's note grid.
- The hamburger NEW/OPEN controls sit side by side; the Projects popup is less rounded, correctly sized, and keeps Rename within the current-project card with a readable input field.
- Create's instrument shortcuts and commands retain minimum touch sizes and horizontal scrolling rather than squashing into narrow buttons.

## Live playback limitations

- Audio Helm MIDI note-on/note-off APIs are frame-timed, not future-DSP scheduled. Edits update notes on the current/following playback cycle, but audio timing is not guaranteed sample-accurate for synths.
- Drum and chop hits already scheduled in the roughly 100 ms DSP lookahead buffer cannot be recalled after an edit or tempo change. Newly edited events take effect beyond this buffered interval.
- When changing notes during playback, already sounding sustained synth notes may be retriggered to synchronize voices with the edited pattern. Track mute cuts held voices; gain applies to following attacks and drum/slice scheduling.
- The sampler reuses the existing player waveform and chop engine, retaining its sample-loading formats and their original platform constraints.

## Test in Unity

1. Compile in the original Unity 2022.3 editor and load `Assets/Scenes/Main.unity`.
2. From Create, trigger a beat, press Play, and add or remove steps without stopping. Check the Create playhead.
3. In Arrange, duplicate a clip and confirm numbering, then change the BPM slider and loop bar buttons while playing.
4. In Mix, adjust track gain, mute/solo and all eight drum faders. Verify drum volumes at the Unity AudioMixer output.
5. In Sound select Pads and press next/previous preset repeatedly across list boundaries.
6. In Create tap SAMPLER, select a sample, listen, auto-slice and apply 16 chops; return to Chops to sequence them.
7. Open Projects and verify New/Open and Rename remain inside the visible safe area and saves survive reopening.
8. Run **ZPunk → 2.0 → Run Live DAW Document Checks**.

Static checks and archive tests are not a substitute for Unity build or device runtime tests.
