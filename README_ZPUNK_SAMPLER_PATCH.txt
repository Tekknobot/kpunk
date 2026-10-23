ZPUNK Sampler Interaction + Transient-Safe Warp Patch
2026-10-21

INSTALL
Copy the Scripts/ and KMusic/ folders in this ZIP into your Unity project's
Assets/ directory. Replace the matching .cs files. No scene, prefab, or UXML
changes are required. Keep the original Unity .meta files.

FILES PATCHED
- Scripts/DAW/ZPunkSampleWarp.cs
- Scripts/DAW/ZPunkClipEditorUI.cs
- Scripts/KMusicPlayerUI.cs
- Scripts/KMusicSampleSequencerUI.cs
- KMusic/UI/Controls/WaveformView.cs

CHANGES
- DAW sampler pads select their corresponding start marker, audition only that
  bounded slice, and frame the segment in the waveform.
- Chop pads display time ranges and a miniature start/end range bar; selected
  pads and selected waveform segments highlight together.
- FULL restores the entire sample. Existing zoom, pan, and pinch still work.
- Asynchronous attack analysis draws amber (kick-like) and violet (snare-like)
  *estimated* ghost guides without modifying or saving actual chop markers.
- HIT SNAP prioritizes nearby detected attack guides, falling back to the 1/16
  beat grid. GRID SNAP uses only the beat grid. Toggle choice is retained.
- Marker dragging/adding, scrubbing, time readouts, nudging, and the visible
  warp waveform now use the active (possibly warped) clip timeline consistently.
- Warp reintroduces original PCM around onset cores with crossfades to reduce
  softened/doubled attacks from WSOLA. Nontransient material remains stretched.
- Source BPM can be changed in steps of 1 or 10 to make correction faster.
- Existing saved chops and their serialized format are unchanged.

IMPORTANT AUDIO NOTES
This is conservative attack preservation, not spectral source separation or a
fully transparent modern time stretcher. Large BPM changes, dense mixes, vocals,
reverb, and sustained material can still artifact. Turning WARP OFF plays the
unmodified source. Check SOURCE BPM explicitly: the initial auto value is a
*duration estimate*, NOT a measured beat-detection BPM. An incorrect source BPM
is a major cause of poor-sounding warp.

CHECK IN UNITY / ON DEVICE
1. Import a clean 4-bar drum loop. Confirm ghost hints appear after analysis.
2. Place two start markers and tap pads S01/S02. Check framing, shaded range,
   scrub position, time ranges, selected colors, and one audition per tap.
3. Drag a start marker close to a ghost: HIT SNAP should favor the nearby hit;
   GRID SNAP should prefer 1/16 beat lines. Neither moves other markers.
4. Check +/- zoom, FULL, pan, and pinch; change tempo while a pad is selected.
5. Set correct source BPM, enable warp, inspect progress and listen for transient
   attacks. Compare WARP ON/OFF, short loops, long songs, mono/stereo, and iOS/
   Android builds, including repeated quick tempo/sample changes.

VALIDATION
Source edited and statically checked for balanced C# punctuation. Unity
compilation, builds, device playback, and listening tests were not available.
