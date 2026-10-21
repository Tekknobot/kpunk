ZPUNK Transient-Grid Warp and Progress Patch

Merge the Scripts folder into your Unity project's Assets folder.
Modified:
  Scripts/DAW/ZPunkSampleWarp.cs
  Scripts/DAW/ZPunkClipEditorUI.cs
  Scripts/KMusicPlayerUI.cs

WHAT CHANGED
- Previous warping only time-stretched the full sample according to a duration-derived BPM guess.
- New transient analysis looks for prominent low and high-band percussive attacks.
- When strong attacks fall near eighth-note positions, an alignment map moves them onto the grid conservatively.
- Pitch-preserving overlap/add synthesis follows that map; the original audio remains unchanged.
- Sampler shows processing stage, actual 0-100% work progress, and a slim progress bar.
- Long clips are handled in bounded PCM chunks, with cancellations and overlap between processing chunks.

IMPORTANT
- The source BPM shown by the UI is still initially estimated from clip LENGTH, not verified beat detection. For full tracks, set the correct source BPM using the available controls to avoid bad tempo matching. The new patch intentionally labels this LENGTH EST.
- This is transient-guided time warping, not kick/snare stem separation. A dense mix, vocals or irregular percussion may produce imperfect anchors.
- Avoid large BPM conversions on long files where processing time and memory use may be substantial.
- Static checks and a synthetic attack-detection/grid test passed; Unity compilation, realtime behavior and device listening have NOT been verified.

TEST ON GALAXY S22
1. Load a clean 4- or 8-bar kick/snare drum loop with a known original BPM.
2. Set source BPM correctly; set song BPM to a different value.
3. Enable WARP. Check stage/percentage/progress bar and final GRID ALIGNED / N ATTACKS status.
4. Listen for attack alignment, preserved pitch, and absence of clicks at processing block boundaries.
5. Repeat with WARP OFF, a complete song, and by switching samples or BPM while processing.
