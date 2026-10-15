# ZPunk 2.0 — Mobile Arrangement UI Rebuild

Base: Archive(20261015-142101).zip (latest uploaded project)

## What was broken in the source

1. The runtime stylesheet forced `.km-root` to a fixed `1024px x 2048px` regardless of phone safe area, then a separate root element applied safe insets. This produced nested overflow, scrollbars, and clipped controls.
2. The phone layout multiplied UI dimensions by as much as 2.4x using `resolvedStyle.width / Screen.width` even though the PanelSettings had already scaled Unity's logical coordinates. This inflated transport controls, context buttons, and arrangement rows.
3. The application's font recovery script replaced UI Toolkit's theme font with an OS/built-in font. The screenshot is consistent with font glyphs failing in the Device Simulator: control backgrounds appeared with little or no text.
4. Existing USS styling used CSS `var()` tokens and unsupported shorthand combinations without guaranteed fallback colors. Replaced with explicit colors and individual border properties.
5. Main.unity serialized pointers to the live UXML and USS used GUIDs missing from the supplied package. Reconnected them to the actual Assets/Resources/KMusic/UI assets.
6. The arrangement was nested inside a vertical-and-horizontal page scroller on top of its own horizontal timeline scroll. Removed the page-level horizontal scroller and hid the extra scrollbar when ARRANGE is selected.
7. The two UI validation commands were checking a non-existent source path. They now inspect the active Assets/Resources/KMusic/UI/Main.uxml.

## Changed files

- Assets/KMusic/KMusicApp.cs: responsive cloned PanelSettings, safe insets, built-in theme text handling.
- Assets/Resources/KMusic/UI/kmusic_dark.uss: concrete style values and responsive full-panel root.
- Assets/Scripts/KMusicTabsUI.cs: no CHAIN destination, correct 3-workspace navigation, compact transport, no double scaling or page-level horizontal scrollbar.
- Assets/Scripts/DAW/ZPunkArrangementUI.cs: adjusted timeline sizing for the new phone logical coordinates, compact command rows, no visible raw scroller.
- Assets/Scenes/Main.unity: references the real runtime UI and stylesheet asset GUIDs.
- Assets/Editor/ZPunkWorkspaceChecks.cs and ZPunkSafeAreaWorkspaceChecks.cs: correct asset validation paths.

## Expected UI

Portrait phone: 3-workspace bottom navigator (ARRANGE, CREATE, SOUND), concise top transport, song tools, fixed track names / horizontal song timeline and visible clips, with the full UI respecting Screen.safeArea. No fixed 1024px canvas or phantom page-level horizontal scrollbar.

Landscape/desktop: a compact left workspace navigator with the same pages, at a 1280x800 logical design resolution.

Notes: the arrangement editor is Phase 3A/3B. Clips are moved, duplicated, resized and played on the NEXT transport restart. Full MIDI piano roll is still planned for Phase 3C. No changes to Audio Helm synthesis, DrumMixer, clip data, or project serialization.

## How to test

1. Open the extracted folder (Unity version from source ProjectSettings) as a new project, or replace Assets/, Packages/, and ProjectSettings/ in a BACKED-UP project.
2. Open `Assets/Scenes/Main.unity`.
3. Enter Play Mode with the Device Simulator in a notched iPhone portrait profile.
4. Confirm labels appear ('ZPUNK', 'PLAY', 'STOP', 'ARRANGE', 'CREATE', 'SOUND', plus instrument track names), bottom dock is clear of the home indicator, no white vertical page scrollbar, and arrangement lanes fit inside the screen.
5. Use ZPunk > 2.0 > Validate Workspace UI and ZPunk > 2.0 > Validate Safe Workspace.
6. Press DAW ON, enter an arrangement containing known drum events, and audition by Stop then Play.
7. Also test landscape and a desktop Game view.

Verification here: XML parsing, scene-asset GUID references, source structural checks, archive integrity. Unity was not installed in this environment; on-device rendering, asset compilation, and audio runtime tests are not verified here.
