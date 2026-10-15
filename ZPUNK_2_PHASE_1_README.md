# ZPunk 2.0 — Phase 1: Song Document & Legacy Pattern Conversion

This patch adds the foundations for ZPunk's future multitrack arrangement editor **without changing the existing UI, synth engines, or sequencer playback**.

## Added

- `Assets/Scripts/DAW/SongData.cs` — serializable, versioned song, five independent tracks (Drums, Bass, Synth, Pads, Chops), timeline clip placements, shared/linked clip content, arbitrary-tick MIDI notes, drums, chop triggers, and instrument metadata.
- `Assets/Scripts/DAW/LegacySongConverter.cs` — imports the existing pattern bank plus chain into independent clip lanes, preserving blank bars, repeats, held notes, chord voicings, drum velocity tiers, and sample slice IDs. Empty/unassigned chain imports the live bar instead.
- `Assets/Editor/ZPunkSongMigrationChecks.cs` — Unity Editor checks for repeated patterns, bar placement, held notes, minor chords, drum tiers, chops, live-bar fallback, and JSON round trip.

## Modified

- `Assets/Scripts/KMusicProjectManager.cs` — saves a **song snapshot** as a new `song` field inside the existing JSON project file, and exposes `CurrentSong` for subsequent DAW phases. Old project fields and all previous save/load/playback paths remain. Old project slots without a song field are converted in memory when loaded and are not rewritten unless saved normally.

**Important:** During this phase the legacy grid and chain remain authoritative. Each Save creates a new song snapshot from the current legacy state; the new clips are NOT yet playable or directly editable, and the UI will look unchanged. The new schema (`schemaVersion: 2`) is a nested field; the legacy project version is intentionally unchanged.

## How to open and verify

1. Back up your original project and saved projects (`Application.persistentDataPath/Projects`) before opening this copy.
2. Open the included Unity 2022.3.62f2 project. Unity will rebuild the omitted `Library/` cache.
3. In Unity's menu run **ZPunk > 2.0 > Run Song Migration Checks**. You should receive a success dialog; investigate Console errors if not.
4. Open ZPunk, load an existing song, and verify playback remains unchanged. Save it to a new slot and inspect `project_XXX.json`; the new root-level `song` object should contain `tracks` and `contents`.
5. Verify old saves still open, synth controls and drums still play, and the legacy MIDI export still happens.

## Constraints / next patch

- Audio Helm timing, the old global pattern chain, and the existing MIDI exporter have **not** been changed. The new song document is a migration mirror, **not** a functional DAW yet.
- `song` MIDI pitches match the live Helm editor mapping (base MIDI 21). The old exporter has a different key-note base mapping; it has not been modified in this non-destructive patch.
- Bass is a newly created empty lane; it has no allocated Audio Helm engine yet.
- Native Unity Editor compilation and audio playback were not run in this environment. The included Editor checks are intended to run within Unity.
- Next phase: make a song-level transport and clip scheduler that reads `CurrentSong`, without introducing parallel clock ownership.

## Packaging

This is a clean Unity source project containing Assets, Packages and ProjectSettings. The `Library/`, Logs, UserSettings, `.git/` and macOS metadata were intentionally omitted. All original assets and native plugins under Assets are retained.
