ZPUNK 2.2.2 | 45-PATCH SYNTH + STYLE-FOCUSED BASS/PADS
===========================================================

FULL REPLACEMENT - INSTALLATION
1. Back up Assets/StreamingAssets and the 3 named .cs files in the ZIP.
2. Extract the ZIP. Copy BOTH StreamingAssets/ and Scripts/ folders into
   the Unity project's Assets/ folder, MERGING their contents.
3. Do NOT delete the rest of Assets/ and do NOT remove unrelated files.
4. Keep the .meta files alongside the assets and scripts.
5. Allow Unity to reimport assets; check for compilation errors.

BANKS
* SYNTH: 45 curated polyphonic ZOR patches: 30 original ZOR entries were
  level-balanced and 15 NEW varied 90s sounds added. All 45 have 8 voice
  polyphony, legato off, portamento off and no built-in arpeggiator.
* BASS: 24 NEW curated monophonic ZOR presets, six each focused on
  techno, hip-hop, French house and classic house.
* PADS: 20 NEW curated 8-voice ZOR pads, five each in the same four styles.
* Factory BASS and PAD presets remain on disk and in the global index so
  OLD PROJECTS can still request them. They no longer cycle in the curated
  BASS and PADS browsers. Old project sounds appear as (Legacy) while used.
* SYNTH browser behavior from previous 2.2.2 patch is retained: Synth/
  only, plus hidden Lead/Keys legacy project compatibility.
* Other original Helm banks, licenses, index entries, and Unity metadata
  remain intact. CHOPS/DRUMS/audio samples are not changed.

CODE CHANGES INCLUDED
* Scripts/KMusicHelmPresetPickerUI.cs - maintain isolated SYNTH bank and
  historical Lead/Keys project compatibility (same source as prior patch).
* Scripts/DAW/ZPunkInstrumentRack.cs - curated ZOR BASS selector, with
  preserved direct loading of older Bass/ project presets.
* Scripts/KMusicPadSynthRuntime.cs - curated ZOR PADS selector, with
  preserved direct loading of older Pad/ project presets.

LEVEL BALANCING
The existing SYNTH master levels were mostly 0.62 even when individual
oscillators were very quiet. Settings now use conservative per-patch gain
adjustments (~0.68-0.80) and selective oscillator boosts. New BASS uses
~0.76-0.86 and new PADS ~0.74-0.79 master levels. These are Helm parameter
settings, NOT measured output dB or LUFS. Audible loudness varies with note,
filter cutoff, polyphony, velocity and the ZPUNK channel faders.

DEVICE TESTING RECOMMENDED
- SYNTH: play 1/3/5/8 simultaneous notes on all new and old patches;
  compare patch loudness, clipping and CPU usage on real Android and iOS.
- BASS: A2 groove at 90, 124 and 135 BPM. Listen for clicks on short notes.
- PADS: hold 4-note chords across adjacent clips/loop boundaries; check
  reverb tails, release overlap, levels and polyphony.
- Restore old projects that used factory Bass/ and Pad/ paths, then select
  the next curated patch and save. Verify original path restored on reload.
- Compare synth against drums on phones and lower-end tablets.

VALIDATION
Static checks performed: JSON parsing, every indexed path exists, no
index duplication, unique Unity metadata GUIDs, correct bank counts,
polyphony, restricted selection paths and archive CRC integrity.
No actual Unity compilation, physical device playback, or rendered audio
loudness analysis was possible in this environment.

BACKUPS
Factory Bass/Pad files remain in the folder and index: do not delete them
when applying this update. Only the SYNTH browser's pre-existing script
change depends on installing KMusicHelmPresetPickerUI.cs from this ZIP.

UI PATCH NAME LABEL FIX
The BASS and PADS interfaces show the branded preset filename only:
  Bass/ZOR Techno Acid 303.helm -> ZOR Techno Acid 303
  Pad/ZOR Techno Detroit Strings.helm -> ZOR Techno Detroit Strings
SYNTH already uses this behavior. The preset index and all .helm paths
are UNCHANGED, protecting audio loading and existing saved projects.
