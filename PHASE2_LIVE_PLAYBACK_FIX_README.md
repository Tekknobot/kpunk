# ZPunk 2.0 Phase 2 — live-pattern playback correction

Fixes a Phase 2 regression where DAW ON played an old/saved snapshot instead of the currently entered 16-step beat, and the legacy grid playhead stopped moving.

Changes:
- Captures current drum/synth/pad/sampler steps in memory when pressing PLAY in DAW mode, without requiring a project save.
- If the legacy pattern chain is OFF, plays the active 16-step pattern (ignores stale chain slots).
- If legacy chain is ON, uses its assigned pattern slots as a preview arrangement.
- Automatically loops legacy live/chain previews, while genuine DAW-authored arrangements retain their own settings.
- Drives the existing step-grid playheads from the DAW transport, including reset on STOP.
- Warns if there are no playable events.
- Adds a regression case for a four-on-the-floor beat plus pad notes to the Unity Editor Phase 2 checks.

Apply this ZIP at the Unity project root, merging Assets/, overwriting the four .cs files. No Audio Helm, scene, or mixer changes in this hotfix. The previous Phase 2 drum gain-staging fix remains in KMusicDrumSequencer.cs.

Verification: static checks and ZIP integrity; NOT compiled or auditioned in Unity. Use Unity menu ZPunk > 2.0 > Run Phase 2 Arrangement Checks, then test DAW ON with 4-on-the-floor drums without saving first.
