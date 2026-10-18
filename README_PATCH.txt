ZPUNK / Unity 6.5 Intel -- replacement patch
==============================================
Source: Archive(20261018-160823).zip

INSTALL
1. Stop Play mode and close Unity.
2. Back up / commit the unity6-upgrade worktree in GitKraken.
3. Extract this archive into your Unity project ROOT, preserving the Assets/ paths.
   Replace matching files (do not delete the existing Assets folder).
4. Reopen Unity 6000.5.5f1 Intel. Let it import the new StreamingAssets files.
5. Do not merge C# code by hand: replace the two complete C# files.

CHANGES
- ZPunkClipEditorUI: automatically frames the actual MIDI notes when a
  pattern is opened, its track is selected, or a new pattern is generated.
  Tries to show the full note span, or the densest group if too wide for
  the viewport. Ordinary editing/dragging keeps manual scroll position.
- ZPunkSoundUI: distortion ON/OFF uses Helm's binary native parameter;
  TYPE uses an exact 0..3 native detent; DRIVE and MIX retain the normalized
  live path. Avoids repeated identical writes during a drag and holds an
  immediately edited UI value very briefly while native read-back catches
  up. No background native retries; presets remain the initial settings.
- StreamingAssets/HelmPresets/Bass: ten new ZOR bass patches and .meta files.
  The preset index is updated so they appear in the normal BASS browser.
  All 10 are monophonic, A1-playable (no octave transpose), and begin with
  distortion/delay/reverb off for clean A1 low-note sound.

ZOR PRESETS
ZOR Velvet Sub, ZOR Blue Current, ZOR Clear Pulse, ZOR Metro Pluck,
ZOR Satin Square, ZOR Glassline, ZOR Roundabout, ZOR Hollow Motion,
ZOR Rubber Circuit, ZOR Night Runner.

CHECK AFTER INSTALL
- Open a BASS or SYNTH clip with MIDI notes far from the old octave;
  confirm the note group is visible without scrolling.
- Generate a new melody or chord pattern and confirm auto-focus.
- Adjust distortion TYPE, DRIVE and MIX live; turn OFF then ON and confirm
  the exact settings remain, including on rapid consecutive toggles.
- Browse 10 ZOR presets with BASS selected; play sustained and short A1
  notes (MIDI 33) at comfortable volume and adjust output levels to taste.
- Verify Android & iOS builds on actual devices before submitting.

VALIDATION / LIMITS
JSON, preset index, metadata and C# structural checks passed. Cannot
compile or audition inside Unity here; Unity 6 playback and iOS/Android
runtime testing are required. These are programmed preset designs, not
claimed to be audio-auditioned or mastered.
