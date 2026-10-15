# ZPunk 2.0 — Manual Chop and Waveform Update

## Changes
- Chops in Create: manually add/delete snapped markers. Auto 4/8/16 slice buttons removed.
- A sixteenth-note ruler follows the song BPM, zoom and horizontal waveform navigation.
- The closing marker (END 16) is visible at the right boundary after 16 slice starts.
- New chops are scheduled monophonically; prior slices end at next scheduled trigger.
- Green Sampler shortcut removed. Use CHOPS in Create and the SAMPLE action.
- Sixteen chop audition buttons remain available inside a vertically scrollable pad area.
- Arrangement keeps absolute lane/bar dimensions across phone and tablet viewports.
  Scroll horizontally instead of resizing bars to fill the visible screen.
- Sample browser is styled as a centered, safe-area constrained panel.
- Warp conversion works incrementally without a fixed 40-second limit, with
  musical-time-stretched audio generated over frames to reduce stalls.
- Waveform peaks are generated in fixed-size chunks (not by loading entire long
  samples into a temporary float array).
- Existing green edit-clip playhead remains in Create while song playback runs.

## Reality / limitations
- Native Unity compilation and phone audio playback could not be run here.
- There is no fixed loop-length limit, but Unity AudioClip allocation, media
  decoding and available memory still impose device-dependent limits.
- The WSOLA chunked stretcher fades short chunk boundaries to limit clicks;
  long recordings need on-device listening and quality checks.
- Synth and note playback/other ZPunk functions remain unchanged except where
  listed above. Chops already queued within DSP lookahead may finish on live edit.

## First checks in Unity
1. Compile with Unity 2022.3.62f2 (the version in ProjectSettings).
2. Create a CHOPS clip, program S01 and S02 at adjacent steps, and verify one
   chop stops at the next start while arranging and live editing.
3. Open SAMPLE, load audio, place 16 manual markers, observe END 16,
   adjust +/- ZOOM and watch bar numbering respond.
4. Test SAMPLE > BROWSE on phone/tablet safe area, including search and scrolling.
5. Test WARP on an audio clip longer than 40 seconds and monitor memory.
6. Rotate Device Simulator and verify arrangement scrolls without stretched bars.
