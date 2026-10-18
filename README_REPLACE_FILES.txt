ZPUNK DISTORTION CONTROL FIX (2026-10-18)

Unzip this archive and copy its Assets folder INTO your Unity project's existing Assets folder.
Choose Replace/Overwrite existing files. DO NOT merge C# source code text.
Do not delete your project Assets directory, and keep any newer code backups before replacing.

Updated complete files:
Assets/Scripts/DAW/ZPunkSoundUI.cs
Assets/Scripts/KMusicHelmSynthBinder.cs
Assets/Scripts/KMusicPadSynthRuntime.cs
Assets/KMusic/KMusicApp.cs

Changes:
- Distortion ON uses the native Helm 0/1 switch.
- Legacy distortion, delay and reverb buttons use UI Toolkit ClickEvent, not PointerDown.
- Drive sends exact -30..+30 dB native values; 100% => +30 dB.
- Type sends exact native integer 0..3.
- Native drive/type/ON readback keeps the bus aligned with presets.
- Bypassing does not overwrite drive, mix or distortion type.

Native Unity plugin binaries are NOT changed.

Recommended quick test: Load a distortion-enabled preset, record the displayed DRIVE
value, toggle OFF and ON several times, and verify the sound/DRIVE/TYPE/MIX do
not drift. Max DRIVE should display +30 dB, center 0 dB, min -30 dB.

Note: Code was statically verified and the included Linux Helm plugin's metadata
reports drive limits of -30 to +30 dB. Unity/device playback is not available here.
