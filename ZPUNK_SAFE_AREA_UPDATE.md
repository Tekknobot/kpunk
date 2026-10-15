# ZPunk 2.0 - Safe Area / Mobile Workspace Update

This release uses the uploaded October 15 project as a baseline.

- Removed the visible Chain destination (legacy chain loading remains available internally).
- Added three destinations: Arrange, Create, Sound.
- Safe-area padding uses Screen.safeArea on all four sides (camera cutout, round corners, gesture area), converted to panel logical units.
- Added a font fallback and readable foreground colors for mobile labels.
- Rebuilt the workspace to keep the arrangement outside the legacy scrolling pages.
- Made the arranger's toolbars horizontally scrollable and added large touch controls for clip movement and resizing.
- Changed the narrow display to a bottom dock and wider displays to a side navigation rail.

## Installation
Open this folder as a Unity project. Keep your original project backed up; do not merge into a project with an older UI patch.

## Verification in Unity
1. Run Main scene in a portrait mobile simulator; verify title, transport and bottom dock are inside the safe area.
2. Check all labels are readable and Arrange displays track rows.
3. Open Create/ Sound and return to Arrange.
4. Test drum playback in DAW mode. Changes to clips are still auditioned after Stop and Play.

## Caveat
This environment cannot run Unity's Editor or iOS simulator; in-Editor testing is required. This update changes UI layout, not the audio scheduling engine.
