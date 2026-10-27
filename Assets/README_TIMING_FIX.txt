ZPUNK — Playback Timing / Retrigger Reliability Patch
Source: Archive(20261027-033540).zip (October 27, 2026)

Replace in Unity Assets/ with:
  Scripts/DAW/ZPunkSongTransport.cs
  Scripts/KMusicDrumSequencer.cs

Changes
1. BASS / SYNTH / PADS — when one MIDI note ends exactly as another
   of the same pitch starts, dispatch its NoteOff up to 25 ms early.
   This reduces collapsed retriggers from off/on commands arriving
   during the same Unity frame. MIDI and saved song data are unmodified.
2. DRUMS / CHOPS — recover sample triggers arriving up to 65 ms late,
   rather than discarding anything >20 ms late. Skip only severe hitches
   to avoid large catch-up flams.
3. DRUMS — track reserved DSP voice lifetimes; avoid cutting a playing
   or queued drum AudioSource when scheduling a new hit. Dynamically
   expand busy drum lanes to 8 voices max, only when necessary.
4. Optional timing diagnostics — set verbose=true on ZPunkSongTransport
   to print every 8 seconds when note dispatch or sample recoveries
   indicate a frame stall.

Regression testing on an actual device
  * Repeated 16th notes of the same pitch across multiple loop cycles
    on BASS, SYNTH, PADS at BPM 90, 120, 150, and 180.
  * Mixed chords, overlapping different pitches, and sustained notes.
  * Kick/hats in 16ths, then a simultaneous drum hit pattern.
  * CHOPS adjacent 16ths; check alignment and choke behavior.
  * Compare normal PLAY vs Collect & Export stems, and loop-on/off.
  * Make edits while playing; test mute/solo and project switches.

Limitations
  * HelmController's public NoteOn/NoteOff is still main-thread,
    not DSP/sample-accurate. Retrigger handling is a mitigation, not
    a replacement for a native Helm sequencer scheduling redesign.
  * Unity compilation and Android/iOS audio tests have NOT been run.
