ZPUNK 2.2.2  |  90s POLYPHONIC SYNTH BANK (FULL REPLACEMENT)
================================================================
INSTALL
  1. Back up your project and existing Assets folders.
  2. Extract BOTH included StreamingAssets/ and Scripts/ folders into the
     Unity project's Assets/ folder, merging/replacing files as prompted.
  3. Do not replace your entire Assets folder; merge the supplied subfolders.
  4. Keep .meta files intact so Unity references remain stable.
  5. Build and test on iOS and Android, especially lower-end devices.

CHANGES
  - 30 curated ZOR 90s-style melodic SYNTH sounds in HelmPresets/Synth/.
  - Every curated SYNTH file: 8-voice polyphony, no legato, portamento,
    built-in arpeggiation or stutter; max 3 oscillator-unison voices per oscillator
    to help control CPU demand on phones and tablets.
  - SYNTH patch browser now displays Synth/ ONLY, never Bass/ or Pad/.
  - BASS and PADS: their existing presets, paths and shared presets_index.txt
    entries remain untouched, so both dedicated browsers still function.
  - Existing Lead/ and Keys/ presets are retained and made >=8-voice polyphonic,
    with legato disabled, for compatible saved SYNTH projects that reference them.
  - Old SYNTH project Lead/Keys patch selections can still load in legacy mode;
    advancing the patch selector switches to the curated 90s bank.
  - Old SYNTH selections referring to Bass/ or Pad/ now fall back to the
    curated bank (the original files remain for their correct instruments).
  - Factory patch author attributions and license strings preserved, with
    explicit source acknowledgments on each ZOR adaptation.
  - No DRUMS/CHOPS assets were removed.

IMPORTANT
  The SYNTH-specific filtering requires Scripts/KMusicHelmPresetPickerUI.cs
  from this ZIP. Replacing StreamingAssets alone does not remove Bass/Pad
  sounds from the SYNTH patch browser.

  The existing HelmPresetIndexBuilder can regenerate presets_index.txt;
  SYNTH filtering will continue to work since it filters on Synth/.

  No Unity/native AudioHelm render test has been performed here. Presets
  were verified as valid JSON with polyphony and path integrity checks.
