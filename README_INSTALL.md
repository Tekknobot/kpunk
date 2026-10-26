# ZPUNK Factory Drum Collection — 16 curated kits

Prepared for **Unity 6** and the existing `DrumKit` ScriptableObject layout shown in the ZPUNK screenshot.

## How to install (no manual sample-slot assignments)

1. BACK UP your Unity project first. Do not remove the existing `Kit_01`–`Kit_11` assets or their samples.
2. Unzip this archive into the **root of your Unity project** (the directory containing `Assets/`), merging folders. It contains only new files.
3. Open Unity, allow WAV import and script compilation to finish.
4. Choose **Tools > ZPUNK > Validate Factory Drum Samples** (should report 128 assignments, 0 missing).
5. Choose **Tools > ZPUNK > Import Factory Drum Kits**. This creates `Kit_12.asset` through `Kit_27.asset` in the current DrumKits folder and fills all eight AudioClip fields for each one.
6. Check the Inspector for `Kit_12` and confirm that all eight fields are filled. Enter Play Mode and test the ZPUNK drum-kit picker. 

The editor script discovers your existing `DrumKit` ScriptableObject type. It matches the serialized `Kit Name`, `Kick`, `Snare`, `Clap`, `Hat Closed`, `Hat Open`, `Ride`, `Rim`, `Crash` fields based on the Inspector screenshot. **If `DrumKit.cs` uses very different field names or validation, the tool logs an error instead of silently changing existing assets.**

### Safety and reruns

- No originals are overwritten by this ZIP. `Kit_01`–`Kit_11` are left intact.
- The importer refuses to overwrite a `Kit_12`–`Kit_27` asset unless its existing `Kit Name` matches the manifest's expected factory kit name.
- Re-running the importer refreshes the new kit references if they are the same factory kits. **It intentionally reapplies curated audio choices to those kits.**
- This installer creates assets, but whether the kits appear at runtime depends on how your ZPUNK kit-selection script discovers kit assets. If that script hardcodes exactly 11 kits or specific paths, it must also be patched. Please provide the loader script to verify.

### Collection contents

- 16 eight-voice kits, each with `Kick / Snare / Clap / Hat Closed / Hat Open / Ride / Rim / Crash`.
- 128 clip assignments drawing from deduplicated WAV files (shared samples are stored once), 44.1 kHz, 16-bit mono, same sample rate/channels as input.
- Folder: `Assets/Resources/KMusic/Drums/DrumKits/ZPUNKFactorySamples/`.
- Unity import manifest: `Assets/ZPUNKKitFactory/KitManifest.json`.
- Source/processing audit: `ZPUNK_Kit_Sample_Map.csv` at ZIP root.

### Audio treatment

Original audio is retained as one-shot samples: no pitch shift, compression, EQ, time-stretching, sample-rate conversion, or onset trimming. Peak ceilings were applied **only by reducing volume** when needed (kick -2 dBFS; snare -3.5; clap -4.5; closed hats -8; open hats -7; ride -8; rim -7; crash -8). Tiny end fades were applied only where a sample ended abruptly. These are practical initial levels, not an ear-verified final master. The source filename, machine and sample assignment are in the CSV. Some 8-voice layouts have hybrid contributions because the original vintage machines did not include every required voice.

### Licensing and listening checks

The archive provided by the user contained no verified redistribution license for these samples. **Confirm that your original sample packs allow redistribution in a paid app** before shipping them in ZPUNK. These selections were curated from source naming, format, length and basic signal measurements; audition them in Unity to make the final musical choices.

---

Original supplied ZIP: 554 valid WAVs from 10 machine folders. Ableton `.asd` files and `__MACOSX` metadata were excluded. Existing 11 kits were not supplied in the ZIP and were not modified.
