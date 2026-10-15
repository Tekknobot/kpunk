# ZPunk 2.0 — Clip Range, Layout & Custom Loop Fix

Applied to the latest uploaded Scripts / UI Toolkit archive, using the previous complete project for unchanged audio assets, prefabs, plugins and scenes.

## Changes
- Full MIDI 0–127 piano roll for Bass, Synth and Pads, with octave buttons scrolling within the continuous 128-row grid instead of hiding notes outside a 24-row window.
- Drum labels mapped exactly to the existing DrumKit (Kick, Snare, Clap, Closed Hat, Open Hat, Ride, Rim, Crash), in playback lane order.
- Chop editor supports slices S01–S16; scheduler discards invalid slice IDs above 16.
- Piano-roll note/step row labels share the same fixed-height grid geometry as the event rows.
- Instrument chips and editing buttons have no flex shrinking; horizontal scrolling handles the overflow on narrow screens.
- Notes/triggers are added on a completed tap, never during a scrolling gesture.
- Arrange adds **LOOP BARS** minus/plus controls (1–256 bars). Adjusting this turns looping on, with an explicit end independent of the total song length. Custom loop lengths persist in the SongData JSON and are not overwritten when the arrangement grows.
- Automatic loop length (before manual selection) follows the end of the last clip, avoiding unintended extra trailing bars.
- Project rename and sample-browser search input fields use dark text on light surfaces for legibility.

## Editor checks
Run **ZPunk > 2.0 > Run Clip Range and Loop Checks** after import. Existing Phase 2 and Phase 3 arrangement checks are also available.

## Known limitations
Unity Editor, Device Simulator and Audio Helm runtime tests cannot be executed in this environment. Test the exact phone display and audible clips in Unity. Arranged clips remain snapshot-based during playback: stop and play to hear edited notes.
