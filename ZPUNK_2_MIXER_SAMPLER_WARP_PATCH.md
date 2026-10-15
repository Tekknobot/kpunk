# ZPunk 2.0 — Live Mixer, Sample Libraries, Warp, Kit Navigation

## Updated features
- The Bass, Synth, and Pads track MIX faders control the actual AudioMixer attenuation rather than only note-on velocity. This changes the loudness of notes already sounding. Three mixer assets now expose independent master attenuation parameters.
- CREATE gets a fixed-height kit selector, PREV/NEXT and current kit name for drums. Changing instruments retains button widths, track-strip horizontal scroll, and vertical placement.
- SOUND > Pads lists only `Pad/` presets and loads these independently of the old hidden Pad UI.
- CREATE > SAMPLER now has WARP ON/OFF, estimated original BPM with +/-1 adjustment, plus a visible folder location.

## Sample library locations
- **Unity Editor**: copy WAV / MP3 / OGG into `Assets/ZPunkSamples/` (nested folders accepted). The prior `Application.persistentDataPath/ImportedSamples` and `Assets/StreamingAssets/Tracks` paths remain readable.
- **iOS**: Files app > On My iPhone > ZPunk. This directory is created when app starts. The iOS document picker imports files there. The included post-build step enables iOS document-file sharing.
- **Android**: public `Music/` directory via MediaStore (Android 11+ scoped storage, runtime audio read permission). Samples in Music no longer have to carry MediaStore `IS_MUSIC=1`; subfolders are included.

## Tempo matching
WARP automatically estimates the source tempo from a short loop's duration and a likely 4/4 bar count, then applies an offline WSOLA-style time stretch at the current arrangement BPM with approximately preserved pitch. This is a heuristic, *not Ableton's transient-aware warp analysis*. Adjust source BPM using +/-1 if the estimate is wrong.

For mobile memory and performance, this only operates on clips up to 40 seconds and 5 million output floats (up to two channels). Longer tracks remain unwarped, and unreadable/compressed audio gracefully falls back to the original recording. Sample source files and original AudioClip remain unchanged. BPM changes during playback re-render after a debounce, which may cause a brief hiccup while rendering; clip edits remain live. Studio-grade real-time audio warping for full songs is future work.

## Recommended test order
1. Launch Unity Editor. Add a WAV loop to `Assets/ZPunkSamples`, open CREATE > SAMPLER > BROWSE.
2. Select the loop and verify status and source BPM; change Arrange BPM and check tempo-matched preview and sample chops.
3. In CREATE > DRUMS switch kit, then switch to BASS/SYNTH/PADS and confirm stable controls.
4. Sustain a Bass, Synth, or Pads note while moving its MIX gain, including at 0%, 50%, and 100%.
5. SOUND > PADS: cycle patches using previous/next and audition notes.
6. On device: verify iOS Files/ZPunk and Android Music MediaStore access; test build-time file permissions.

## Verification status
Source-structure and ZIP checks only. Unity Editor compilation, actual audio rendering and mobile platform storage remain to be tested on device.
