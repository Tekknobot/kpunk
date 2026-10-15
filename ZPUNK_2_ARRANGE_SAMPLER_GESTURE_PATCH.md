# ZPunk 2.0 - Arrangement paging and sampler gestures

This patch updates four existing C# scripts and preserves the rest of the Unity project.

## Arrange
- Removed the horizontally scrolling timeline view and its scrollbar.
- Stable fixed-pixel bar widths, with **< BARS** and **BARS >** to navigate longer songs without horizontally scrolling.
- Zoom and timeline pages update the absolute song grid and keep clip placements intact.

## Create -> Sampler
- One continuous vertical ScrollView for the sampler controls and all 16 chop pads (no nested chop-grid vertical scroller).
- Swiping vertically over the waveform scrolls the sampler page.
- When zoomed, dragging left/right on an empty waveform area pans the visible sample window. Markers and their normalized positions stay in place.
- Tap a chop marker to select it without moving it; drag the marker to reposition with the existing sixteenth-note snapping.
- **DEL SELECTED** deletes the selected marker; **< MARK / MARK >** selects previous/next; **- 10 MS / + 10 MS** nudges selection.
- Selection name and time appear under the marker toolbar. The waveform keeps its selected-marker highlight after pointer release.
- Pinch zoom and mouse-wheel zoom remain available.

## Files changed
- `Assets/Scripts/DAW/ZPunkArrangementUI.cs`
- `Assets/Scripts/DAW/ZPunkClipEditorUI.cs`
- `Assets/Scripts/KMusicPlayerUI.cs`
- `Assets/KMusic/UI/Controls/WaveformView.cs`

## Validation and limitation
Validated balanced code syntax delimiters, UXML parsing, preservation of all unrelated original project assets and ZIP integrity. Unity Editor compilation and physical-device gestures were not available in this environment; please test using the Editor/iOS/Android simulator.
