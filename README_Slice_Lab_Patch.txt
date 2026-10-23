ZPUNK - SLICE LAB / SIMPLIFIED SAMPLER
Patch based on Archive(20261021-141621).zip

Copy the supplied Scripts folder INTO YOUR UNITY PROJECT'S Assets folder.
Keep existing .meta files. This package contains only changed sources.

CHANGED
  Assets/Scripts/DAW/ZPunkClipEditorUI.cs
  Assets/Scripts/KMusicPlayerUI.cs
  Assets/Scripts/DAW/ZPunkSampleWarp.cs

WORKFLOW
  1. BROWSE to load a sample.
  2. Drag HEAD near the first main kick/snare transient (HIT SNAP is the default).
  3. Press SLICE HITS. It now looks for a recurring main quarter-beat pulse
     and preferentially uses strong onsets near those beats. No time-stretch
     is involved. If a stable beat cannot be established, it falls back to
     non-quantized, minimum-spaced hit slices.
  4. Tap a slice pad to select/audition it and focus on its waveform segment.
     Use zoom, ADD MARK, DELETE, and +/- 10ms adjustments to refine cuts.
  5. APPLY CHOPS to assign the slices to the CHOPS track.

LAYOUT
  * Retires non-working sample previous/next arrows and the cluttered
    source BPM, beat-align, auto-BPM, and slice-beats controls.
  * Provides DONE to return to the note editor.
  * PLAY and STOP sit directly above the waveform, alongside HIT SNAP and FULL.
  * Primary SLICE HITS action is prominent; 16 pads stay in a 4x4 grid.
  * Pitch/key information is available below the pad grid.
  * Warp remains opt-in as a secondary WARP OFF/ON button. Opening the new
    Slice Lab forces warp OFF, default preference is OFF; slicing leaves
    original PCM unchanged. Warp quality is not changed by this patch.
  * Transient analysis now provides onset strength, helping to favor prominent
    drum attacks rather than quieter hats in the pulse estimator.

NOTES
  Not guaranteed to quantize live recordings or variable-tempo music.
  When hits are slightly off-grid, chops are anchored to the real attacks;
  the audio itself is NOT moved to the grid. This avoids stretch artifacts.
  Original sample data remains intact.

VALIDATION
  Static structural/syntax-balance and ZIP integrity checks are performed.
  Not compiled in Unity; test Editor, iOS, and Android before release.
