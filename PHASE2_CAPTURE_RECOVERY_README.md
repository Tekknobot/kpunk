# ZPunk 2.0 — Phase 2 capture recovery patch

This patch applies **after** `ZPunk_2_Phase2_Empty_Arrangement_Fix.zip`.

## Changes

1. Empty native song snapshots no longer override the current live grids. A native arrangement containing real clips still takes priority.
2. DAW live capture reads the drum mask, and **only if the entire mask is zero** and the selected drum lane has visibly lit pads, derives a temporary drum pattern from that lane's visible cells. Saved patterns, the mask, and mixer values are not modified.
3. Empty-playback warnings now report raw drum hits, visible cells, number of clip contents, and whether the snapshot was migrated.
4. Editor checks cover the empty-native-song vs. live-beat case.

## Apply

- Back up the project, exit Play Mode, and extract `Assets/` from the ZIP into the project root, replacing the matching files.
- Run `ZPunk > 2.0 > Run Phase 2 Arrangement Checks` after compiling.
- Try drums on steps 1, 5, 9, 13 with DAW ON and Play.
- If still silent, send the **complete** `LIVE CAPTURE EMPTY` warning as well as the new `No playable events after capture` line. The counts will distinguish input binding from conversion errors.

## Limitations

This is a source-level patch. Unity Editor/audio runtime is not available in the execution environment, so compilation and audible playback must be tested in Unity.
