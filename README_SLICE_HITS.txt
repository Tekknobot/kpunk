ZPUNK SAMPLER - SLICE HITS PATCH (2026-10-21)

PATCH THESE FILES INSIDE YOUR UNITY Assets DIRECTORY:
  Scripts/KMusicPlayerUI.cs
  Scripts/KMusicChopState.cs
  Scripts/DAW/ZPunkClipEditorUI.cs

Changes
- Removed GRID 1/2, GRID BEAT and GRID BAR controls from sampler page.
- Added SLICE HITS: place HEAD on a transient and create boundaries at subsequent
  detected ghost hits, up to 16 playable chops.
- Uses original detected attack locations, not BPM-quantized marker positions.
- Ignores detections too close together to make practical chop segments.
- Last detected boundary closes the final pad, even if fewer than 16 are found.
- Preserves closing-boundary metadata when saving/restoring chops and projects.
- If hit analysis is still running, queues the slice using the HEAD position at tap.
- Leaves existing manual markers untouched when no later hits are available.
- Keeps pad focus, playhead seek, marker editing and FULL waveform controls.

How to use
1. Load sample; wait for hit guides (or tap SLICE HITS during analysis).
2. Turn on HIT SNAP and move HEAD to your chosen first kick/snare.
3. Tap SLICE HITS. Pad 01 starts exactly at HEAD; following pads span
   successive detected hit guides.
4. Tap pads to inspect/edit. APPLY to commit to the arrangement.

Notes
- These are detected onsets, not isolated kick/snare stems; busy mixes may also
  have guides on hats, vocals or other sharp sounds. Inspect and move boundaries
  as needed. Slices vary in duration with the recording's timing.
- The operation replaces current markers only when a valid following hit exists.
- Warp remains independent. WARP OFF avoids warp-related processing artifacts.
- Only source/structure checks were possible here. Unity compile, mobile input,
  and playback should be verified locally before release.
