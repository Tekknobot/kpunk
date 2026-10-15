# ZPunk 2.0 — Phase 2: Opt-in Arrangement Playback + Drum Gain Staging

Based on the user's supplied `Archive(20261015-115604).zip` and its Phase 1 DAW song document. This is a source patch, not a Unity-tested build.

## What's implemented

- `SongEventPlanner` compiles independent `SongData` clip lanes into ordered tick events. Supports bar gaps, note pitches/lengths, linked clip content, clip trimming, source offsets, looping clip content, track mute/solo flags and arrangement looping.
- `ZPunkSongTransport` is instantiated on the existing sequencer GameObject. It is opt-in via a **DAW: OFF/ON** button inserted beside Play at runtime (no scene changes needed).
- **DAW: OFF**: current legacy 16-step groovebox Play/Stop and chain logic are retained.
- **DAW: ON**: press **PLAY** to play `KMusicProjectManager.CurrentSong` through independent drum, synth, pad and chop lanes. Press **STOP** to cancel queued samples and silence the synths. Switching mode suspends/restores legacy Audio Helm sequencers to prevent simultaneous duplicate notes.
- Drum and sample-chop hits are scheduled ahead on `AudioSettings.dspTime`. Samples near song-loop boundaries are prefetched into the next cycle.
- Main synth and pad notes use their existing `HelmController` instances. `PauseSong`, `ResumeSong`, `Seek` and `CurrentTick` are available on the runtime component for future arrangement UI integration.
- Preserves DAW-owned song data on saves while DAW mode is selected, rather than regenerating it from the legacy pattern chain. Existing song files are not rewritten on load.
- Uses the original Unity drum mixer/groups. Fixed drum fader double-attenuation: source voices no longer apply a second copy of the same fader gain when mixer-driven. Fader response changed to a logarithmic amplitude taper with default 0.80 = 0 dB and maximum 1.0 = +6 dB.
- New menu: **ZPunk > 2.0 > Run Phase 2 Arrangement Checks**. The Phase 1 migration test remains available.

## Test in Unity

1. Make a backup. Apply the code-only zip to the Unity project root (merge the `Assets` folder), or open the full patched project.
2. Wait for Unity's compile to finish. Run **ZPunk > 2.0 > Run Song Migration Checks** and **Run Phase 2 Arrangement Checks**.
3. In the existing groovebox, verify 8 drum faders, preview hits, mute buttons, Play/Stop and chain behavior. The drums should be noticeably louder at mid fader positions without changing the default master mixer asset.
4. Load a song with a chain and **SAVE** it once to refresh its DAW snapshot. Select **DAW: ON** near Play and press PLAY. It should play placed drum, synth, pad and sample events across chain bars without the old 16-step sequence doubling them. Press STOP. Toggle **DAW: OFF** and confirm original groovebox playback returns.
5. Verify blank bars remain silent, previously repeated patterns play at each assigned bar, and projects still save/load.
6. Listen to dense kick/snare overlap and check that drums do not clip the master when raising the faders to 1.0. The +6 dB maximum is optional, not a default boost.

## Explicit Phase 2 limitations

- **Bass lane** exists in `SongData` but is silent: no third independent Audio Helm synth instance exists in this uploaded project. This is intentionally not faked by sharing the lead or pad synth channel.
- **No arrangement UI yet.** This phase plays song clips prepared by Phase 1. The track/clip editor is Phase 3. CurrentSong is a snapshot, so **Save before entering DAW mode** to capture the latest legacy chain edits.
- **Melodic synth notes are NOT sample-accurate**: Audio Helm's public `HelmController.NoteOn/NoteOff` API does not schedule at future DSP timestamps. They are dispatched on the closest Unity frame. Drums/chops *are* DSP scheduled. Closing this gap requires a native Audio Helm sequencer or plugin-level timing integration.
- **Live tempo change during DAW playback is not yet supported**; tempo is taken from the song when Play is pressed. Stop and restart after a BPM change.
- DAW track mute and solo fields work from the model, but have no dedicated UI until the arrangement/mixer phase. Advanced panning, audio recording, offline audio rendering, and independent bass synth remain future phases.
- UI design is minimal, using a dynamic DAW button beside Play. Mobile/tablet width should be checked in the actual scene.
- There is no Unity Editor/audio runtime in this environment. Packaging and static checks do not substitute for a Unity compilation and listening test.

## Key files

Added:
- `Assets/Scripts/DAW/SongEventPlanner.cs`
- `Assets/Scripts/DAW/ZPunkSongTransport.cs`
- `Assets/Editor/ZPunkDAWPhase2Checks.cs`

Modified:
- `Assets/Scripts/KMusicDrumSequencer.cs` (routing, gains, DAW hits, transport Play/Stop)
- `Assets/Scripts/KMusicProjectManager.cs` (song-save ownership, load stop)
- `Assets/Scripts/KMusicPianoRollStepPainter.cs` (guard native sequence against duplicate DAW playback; expose synth)
- `Assets/Scripts/KMusicChordTrackUI.cs` (guard native sequence against duplicate DAW playback)
- `Assets/Scripts/KMusicPadSynthRuntime.cs` (expose existing pad synth controller)

No native plugins, Unity audio mixer assets, scenes or sample files were modified.
