using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// A non-destructive, arrangement-backed live clip launcher. Every launch is
    /// committed to an unsaved performance song at a transport bar boundary.
    /// The existing DSP/Helm/song scheduler remains the only audio engine.
    /// </summary>
    public sealed class ZPunkSessionUI : MonoBehaviour
    {
        // The bounded performance timeline is kept deliberately modest so
        // replanning on mobile doesn't compile a huge hour-long event list.
        private const int PerformanceBars = 64;
        private const int MaxScenes = 48;
        private const int SceneWidth = 64;
        private const int TrackWidth = 80;
        private const int GoWidth = 52;
        private const int ColumnGap = 4;
        private const int TileHeight = 52;
        private const int PlaybackHeight = 34;
        private const int VisibleSceneRows = 5;
        private const int MixButtonHeight = 36;
        private const int MixButtonGap = 4;
        private const int MixRowHeight = MixButtonHeight * 2 + MixButtonGap;
        // Ableton-style values are musical note values, not fractions of a bar.
        // 1 BAR = 4 beats, 1/2 = 2 beats, 1/4 = 1 beat, 1/8 = half a beat.
        private static readonly string[] QuantizeNames = { "1 BAR", "1/2", "1/4", "1/8" };
        private static readonly long[] QuantizeTicks = {
            SongData.TicksPerBar4, SongData.TicksPerQuarter * 2L,
            SongData.TicksPerQuarter, SongData.TicksPerQuarter / 2L
        };
        private const long BarTicks = SongData.TicksPerBar4;
        private const long PerformanceTicks = PerformanceBars * BarTicks;
        private static readonly Color Bg = new Color(.075f, .092f, .125f);
        private static readonly Color Surface = new Color(.12f, .15f, .20f);
        private static readonly Color Ink = new Color(.91f, .95f, .98f);
        private static readonly Color Muted = new Color(.57f, .65f, .73f);
        private static readonly Color Accent = new Color(.30f, .88f, .74f);
        private static readonly Color[] LaneColors = {
            new Color(.95f,.68f,.27f), new Color(.93f,.44f,.52f),
            new Color(.40f,.65f,.99f), new Color(.66f,.48f,.87f),
            new Color(.28f,.80f,.67f), new Color(.90f,.80f,.34f)
        };
        private sealed class Scene
        {
            public long sourceTick;
            public ClipData[] clips;
            public ClipContentData[] patterns;
        }
        private struct Launch
        {
            public int track;
            public int scene; // -1 is a quantized track stop
            public long at;
        }

        private readonly List<Scene> scenes = new List<Scene>();
        // All commands in the running performance cycle remain in the event
        // plan after triggering; removing them would restart clips at the wrong
        // phase when the next scene is queued.
        private readonly List<Launch> launches = new List<Launch>();
        private readonly Dictionary<int, List<Button>> clipButtons = new Dictionary<int, List<Button>>();
        private readonly List<Button> stopButtons = new List<Button>();
        private readonly List<Button> muteButtons = new List<Button>();
        private readonly List<Button> soloButtons = new List<Button>();
        // Progress is a visual readout of the currently launched clip's loop
        // position; it does not modify transport or quantized launch timing.
        private readonly List<VisualElement> playbackFills = new List<VisualElement>();
        private readonly List<Label> playbackCaptions = new List<Label>();
        private VisualElement page;
        private ScrollView contentScroll;
        private Label status;
        private Button reloadButton;
        private Button quantizeButton;
        private int quantizeIndex; // persisted when Session is hidden or reloaded
        private SongData source;
        private SongData sourceDocument;
        private KMusicProjectManager projects;
        private ZPunkSongTransport transport;
        private int projectSlot = -1;
        private int[] initialSlots = new int[0];
        private bool[] muted = new bool[0];
        private bool[] soloed = new bool[0];
        private long previousTick;
        private float nextRefresh;
        private bool initialized;
        public Action<int> CreateRequested;
        public Action LayoutRebuilt;
        public Action BeforeLayoutRebuild;
        private bool effectsExpanded = true;

        public void Initialize(VisualElement target)
        {
            if (target == null) return;
            page = target;
            projects = FindObjectOfType<KMusicProjectManager>();
            transport = FindObjectOfType<ZPunkSongTransport>();
            initialized = true;
            ShowCurrentProject();
        }

        public void OnShown()
        {
            if (!initialized) return;
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
            if (projects == null) return;
            // Re-entering from ARRANGE should always capture recent clip edits,
            // even if the project manager edited its SongData object in-place.
            // Never replace an ongoing Session performance in response to a tab switch.
            if (source == null || projectSlot != projects.CurrentSlot ||
                (transport != null && !transport.IsSessionArmed))
                ShowCurrentProject();
            else RefreshButtons();
        }

        public void Disarm(bool stopSession = true)
        {
            if (transport != null) transport.DisarmSessionPlayback(stopSession);
            launches.Clear();
            previousTick = 0;
            RefreshButtons();
        }

        private void ShowCurrentProject()
        {
            if (projects == null || page == null) return;
            if (transport != null) transport.DisarmSessionPlayback();
            projectSlot = projects.CurrentSlot;
            scenes.Clear();
            launches.Clear();
            try
            {
                SongData current = projects.GetSongForArrangementEditor();
                sourceDocument = projects.CurrentSong;
                source = current == null ? null : SongData.FromJson(current.ToJson());
            }
            catch (Exception ex)
            {
                source = null;
                sourceDocument = null;
                Debug.LogWarning("[ZPUNK SESSION] Cannot read arrangement: " + ex.Message);
            }
            int trackCount = source != null && source.tracks != null ? source.tracks.Count : 0;
            initialSlots = new int[trackCount];
            muted = new bool[trackCount];
            soloed = new bool[trackCount];
            for (int i = 0; i < trackCount; i++)
            {
                initialSlots[i] = -1;
                muted[i] = source.tracks[i] != null && source.tracks[i].mute;
                soloed[i] = source.tracks[i] != null && source.tracks[i].solo;
            }
            previousTick = 0;
            BuildScenes();
            BuildLayout();
        }

        /// <summary>Share persisted master FX with performance snapshots without
        /// modifying the temporary scene/clip launches or stopping playback.</summary>
        public void MirrorPerformanceSettings(SongData edited)
        {
            if (source == null || edited == null) return;
            source.performanceDrive = edited.performanceDrive;
            source.performanceGate = edited.performanceGate;
            source.performanceCrush = edited.performanceCrush;
            source.performanceRepeat = edited.performanceRepeat;
            source.performanceGateRate = edited.performanceGateRate;
            source.performanceRepeatRate = edited.performanceRepeatRate;
        }

        private void BuildScenes()
        {
            if (source == null || source.tracks == null) return;
            var starts = new SortedSet<long>();
            foreach (TrackData track in source.tracks)
                if (track != null && track.clips != null)
                    foreach (ClipData clip in track.clips)
                        if (clip != null && clip.lengthTicks > 0)
                            starts.Add(Math.Max(0, clip.startTick));
            foreach (long tick in starts)
            {
                if (scenes.Count >= MaxScenes) break;
                var scene = new Scene { sourceTick = tick,
                    clips = new ClipData[source.tracks.Count],
                    patterns = new ClipContentData[source.tracks.Count] };
                for (int t = 0; t < source.tracks.Count; t++)
                {
                    TrackData track = source.tracks[t];
                    if (track == null || track.clips == null) continue;
                    foreach (ClipData clip in track.clips)
                    {
                        if (clip == null || clip.startTick != tick || clip.lengthTicks <= 0) continue;
                        ClipContentData pattern = MakeLoopPattern(track.kind, clip, t, scenes.Count);
                        if (pattern == null) continue;
                        scene.clips[t] = clip;
                        scene.patterns[t] = pattern;
                        break;
                    }
                }
                scenes.Add(scene);
            }
        }

        // Normalize the *visible* cropped arrangement clip, not its full source
        // content. A 1-bar crop of a 4-bar piano roll thus loops as a 1-bar clip.
        // Reuse the same compiler as the arrangement to honor source offsets,
        // notes trimmed at boundaries, and ACID accent/slide flags.
        private ClipContentData MakeLoopPattern(TrackKind kind, ClipData clip,
            int trackIndex, int sceneIndex)
        {
            ClipContentData original = source.contents.Find(c => c != null && c.id == clip.contentId);
            if (original == null) return null;
            long length = Math.Max(1, clip.lengthTicks);
            var preview = new SongData { lengthTicks = length,
                tracks = new List<TrackData> { new TrackData { kind = kind,
                    clips = new List<ClipData> { new ClipData {
                        contentId = clip.contentId, startTick = 0,
                        lengthTicks = length, sourceOffsetTick = clip.sourceOffsetTick,
                        loopEnabled = clip.loopEnabled } } } },
                contents = new List<ClipContentData> { original } };
            SongEventPlanner.Plan plan = SongEventPlanner.Build(preview);
            var normalized = new ClipContentData {
                id = "zp_session_" + projectSlot + "_" + sceneIndex + "_" + trackIndex,
                kind = original.kind, lengthTicks = length, generatedTag = original.generatedTag
            };
            foreach (var e in plan.samples)
            {
                if (e.type == SongEventPlanner.EventType.Drum)
                    normalized.drums.Add(new DrumEvent { lane = e.value,
                        startTick = e.tick, velocity = e.velocity });
                else if (e.type == SongEventPlanner.EventType.Chop)
                    normalized.chops.Add(new ChopEvent { sliceId = e.value,
                        startTick = e.tick, velocity = e.velocity });
            }
            var pending = new Dictionary<int, Queue<SongEventPlanner.Event>>();
            foreach (var e in plan.midi)
            {
                if (!pending.TryGetValue(e.value, out var notes))
                    pending[e.value] = notes = new Queue<SongEventPlanner.Event>();
                if (e.type == SongEventPlanner.EventType.NoteOn)
                    notes.Enqueue(e);
                else if (e.type == SongEventPlanner.EventType.NoteOff && notes.Count > 0)
                {
                    var on = notes.Dequeue();
                    if (e.tick <= on.tick) continue;
                    normalized.notes.Add(new NoteEvent { pitch = on.value,
                        startTick = on.tick, durationTicks = e.tick - on.tick,
                        velocity = on.velocity, accent = (on.flags & 1) != 0,
                        slide = (on.flags & 2) != 0 });
                }
            }
            // A timeline slot may reference a valid but wholly silent clip.
            // Do not offer it as a playable launch or run a silent transport.
            if (normalized.notes.Count == 0 && normalized.drums.Count == 0 &&
                normalized.chops.Count == 0) return null;
            return normalized;
        }

        private SongData BuildPerformanceSong()
        {
            var performance = SongData.FromJson(source.ToJson());
            performance.arrangementAuthored = true;
            performance.lengthTicks = PerformanceTicks;
            performance.arrangementLoopEnabled = true;
            performance.loopStartTick = 0;
            performance.loopEndTick = PerformanceTicks;
            performance.loopLengthCustom = true;
            performance.contents.Clear();
            for (int s = 0; s < scenes.Count; s++)
                for (int t = 0; t < scenes[s].patterns.Length; t++)
                    if (scenes[s].patterns[t] != null)
                        performance.contents.Add(scenes[s].patterns[t]);
            for (int t = 0; t < performance.tracks.Count; t++)
            {
                TrackData track = performance.tracks[t];
                if (track == null) continue;
                track.clips.Clear();
                track.mute = muted[t];
                track.solo = soloed[t];
                long position = 0;
                int slot = initialSlots[t];
                foreach (Launch launch in launches)
                {
                    if (launch.track != t || launch.at < position ||
                        launch.at >= PerformanceTicks) continue;
                    AppendSegment(track, t, slot, position, launch.at);
                    position = launch.at;
                    slot = launch.scene;
                }
                AppendSegment(track, t, slot, position, PerformanceTicks);
            }
            return performance;
        }

        private void AppendSegment(TrackData target, int track, int scene, long from, long until)
        {
            if (scene < 0 || scene >= scenes.Count || until <= from) return;
            ClipContentData pattern = scenes[scene].patterns[track];
            if (pattern == null) return;
            target.clips.Add(new ClipData {
                id = "session_" + track + "_" + from,
                name = "LIVE " + (scene + 1), contentId = pattern.id,
                startTick = from, lengthTicks = until - from,
                sourceOffsetTick = 0, loopEnabled = true
            });
        }

        private long NextLaunchTick()
        {
            return transport == null ? BarTicks :
                transport.NextSafeSessionLaunch(QuantizeTicks[quantizeIndex]);
        }

        private void CycleQuantize()
        {
            quantizeIndex = (quantizeIndex + 1) % QuantizeNames.Length;
            if (quantizeButton != null)
                quantizeButton.text = "QUANT " + QuantizeNames[quantizeIndex];
            // Existing queued triggers retain the boundary they were given.
            RefreshButtons();
        }

        private void QueueClip(int track, int scene)
        {
            if (source == null || transport == null || track < 0 || track >= initialSlots.Length ||
                scene < -1 || scene >= scenes.Count ||
                (scene >= 0 && scenes[scene].patterns[track] == null)) return;
            if (scene == -1 && (!transport.IsPlaying || !transport.IsSessionArmed)) return;
            // This first pass never cuts an already-running arrangement at the
            // instant the performer taps a clip. Start a Session from STOP;
            // once Session is running all subsequent clip switches are quantized.
            if (transport.IsPlaying && !transport.IsSessionArmed)
            {
                status.text = "STOP ARRANGEMENT TO START SESSION";
                return;
            }
            if (!transport.IsPlaying || !transport.IsSessionArmed)
            {
                // Starting a new performance establishes a downbeat using the
                // existing transport pre-roll, rather than launching mid-beat.
                launches.Clear();
                for (int t = 0; t < initialSlots.Length; t++) initialSlots[t] = -1;
                initialSlots[track] = scene;
                SongData first = BuildPerformanceSong();
                transport.ArmSessionPlayback(first);
                if (!transport.PlaySong(0))
                {
                    status.text = "NO PLAYABLE EVENTS — CREATE A CLIP FIRST";
                    transport.DisarmSessionPlayback(false);
                    return;
                }
                previousTick = 0;
            }
            else
            {
                long at = NextLaunchTick();
                if (at >= PerformanceTicks)
                {
                    status.text = "SESSION CYCLE END — LAUNCH AFTER LOOP";
                    return;
                }
                QueueAt(track, scene, at);
                transport.ApplySessionPlayback(BuildPerformanceSong(), at);
            }
            RefreshButtons();
        }

        private void QueueAt(int track, int scene, long at)
        {
            // Replace any uncommitted choice for the SAME lane and bar; a
            // command whose deadline has passed is never silently moved earlier.
            for (int i = launches.Count - 1; i >= 0; i--)
                if (launches[i].track == track && launches[i].at >= at)
                    launches.RemoveAt(i);
            launches.Add(new Launch { track = track, scene = scene, at = at });
            launches.Sort((a, b) => a.at != b.at ? a.at.CompareTo(b.at) :
                a.track.CompareTo(b.track));
        }

        private void LaunchScene(int scene)
        {
            if (scene < 0 || scene >= scenes.Count) return;
            if (transport == null) return;
            if (transport.IsPlaying && !transport.IsSessionArmed)
            {
                status.text = "STOP ARRANGEMENT TO START SESSION";
                return;
            }
            if (!transport.IsPlaying || !transport.IsSessionArmed)
            {
                launches.Clear();
                bool playable = false;
                for (int t = 0; t < initialSlots.Length; t++)
                {
                    initialSlots[t] = scenes[scene].patterns[t] != null ? scene : -1;
                    playable |= initialSlots[t] >= 0;
                }
                if (!playable) return;
                SongData first = BuildPerformanceSong();
                transport.ArmSessionPlayback(first);
                if (!transport.PlaySong(0))
                {
                    status.text = "NO PLAYABLE EVENTS — CREATE A CLIP FIRST";
                    transport.DisarmSessionPlayback(false);
                }
                previousTick = 0;
            }
            else
            {
                long at = NextLaunchTick();
                if (at >= PerformanceTicks)
                {
                    status.text = "SESSION CYCLE END — LAUNCH AFTER LOOP";
                    return;
                }
                for (int t = 0; t < initialSlots.Length; t++)
                    if (scenes[scene].patterns[t] != null) QueueAt(t, scene, at);
                transport.ApplySessionPlayback(BuildPerformanceSong(), at);
            }
            RefreshButtons();
        }

        private void StopAll()
        {
            if (transport == null || source == null) return;
            if (!transport.IsPlaying || !transport.IsSessionArmed)
            {
                for (int t = 0; t < initialSlots.Length; t++) initialSlots[t] = -1;
                launches.Clear();
                transport.DisarmSessionPlayback();
            }
            else
            {
                long at = NextLaunchTick();
                if (at < PerformanceTicks)
                {
                    for (int t = 0; t < initialSlots.Length; t++) QueueAt(t, -1, at);
                    transport.ApplySessionPlayback(BuildPerformanceSong(), at);
                }
                else transport.StopSong();
            }
            RefreshButtons();
        }

        private void ToggleMute(int track)
        {
            if (track < 0 || track >= muted.Length) return;
            muted[track] = !muted[track];
            UpdateLiveMix();
        }
        private void ToggleSolo(int track)
        {
            if (track < 0 || track >= soloed.Length) return;
            soloed[track] = !soloed[track];
            UpdateLiveMix();
        }
        private void UpdateLiveMix()
        {
            if (transport != null && transport.IsSessionArmed)
            {
                SongData edited = BuildPerformanceSong();
                transport.ArmSessionPlayback(edited);
                transport.ApplyLiveMix(edited);
            }
            RefreshButtons();
        }

        private static Label Caption(string text, int size, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            return label;
        }
        private static Button ActionButton(string name, Action callback, int width = 70)
        {
            var button = new Button(callback) { text = name };
            button.style.height = 42;
            button.style.width = width;
            button.style.minWidth = width;
            button.style.flexShrink = 0;
            button.style.fontSize = 12;
            button.style.color = Ink;
            button.style.backgroundColor = Surface;
            // Explicit zero margins/padding: Unity's default Button margins
            // otherwise make M/S and STOP start several pixels to the right.
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
            button.style.paddingLeft = 4;
            button.style.paddingRight = 4;
            button.style.paddingTop = 2;
            button.style.paddingBottom = 2;
            button.style.borderTopLeftRadius = 5;
            button.style.borderTopRightRadius = 5;
            button.style.borderBottomLeftRadius = 5;
            button.style.borderBottomRightRadius = 5;
            return button;
        }
        // Every heading, clip, M/S pair and stop uses the identical column
        // wrapper. The column width includes no button margins.
        private static VisualElement Column(int width)
        {
            var column = new VisualElement();
            column.style.width = width;
            column.style.minWidth = width;
            column.style.flexShrink = 0;
            column.style.marginRight = ColumnGap;
            column.style.paddingLeft = 0;
            column.style.paddingRight = 0;
            column.style.justifyContent = Justify.Center;
            return column;
        }

        private static void FillColumn(Button button, int height)
        {
            button.style.width = Length.Percent(100);
            button.style.minWidth = 0;
            button.style.height = height;
            button.style.flexGrow = 0;
            button.style.flexShrink = 0;
            button.style.marginLeft = 0;
            button.style.marginRight = 0;
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
        }

        // Row label treatment follows the compact SCENE tiles: same column
        // wrapper, zero child margins, dark panel and a slim accent stripe.
        private static VisualElement RowBadge(string caption, int height)
        {
            var cell = new VisualElement();
            cell.style.width = Length.Percent(100);
            cell.style.height = height;
            cell.style.minHeight = height;
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.backgroundColor = new Color(.10f, .14f, .19f);
            cell.style.borderTopLeftRadius = 5;
            cell.style.borderTopRightRadius = 5;
            cell.style.borderBottomLeftRadius = 5;
            cell.style.borderBottomRightRadius = 5;
            var stripe = new VisualElement();
            stripe.style.width = 3;
            stripe.style.height = Math.Min(36, height - 12);
            stripe.style.marginLeft = 3;
            stripe.style.backgroundColor = Accent;
            stripe.pickingMode = PickingMode.Ignore;
            cell.Add(stripe);
            var label = Caption(caption, 11, Ink);
            label.style.flexGrow = 1;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.marginRight = 3;
            cell.Add(label);
            return cell;
        }

        private Color TrackColor(int track)
        {
            if (source != null && source.tracks != null && track < source.tracks.Count &&
                source.tracks[track] != null && !string.IsNullOrEmpty(source.tracks[track].colorHex) &&
                ColorUtility.TryParseHtmlString(source.tracks[track].colorHex, out Color color))
                return color;
            return LaneColors[track % LaneColors.Length];
        }

        private void BuildLayout()
        {
            if (page == null) return;
            // Shared FX controls must be parked before clearing this view.
            BeforeLayoutRebuild?.Invoke();
            page.Clear();
            page.style.backgroundColor = Bg;
            page.style.paddingLeft = 8;
            page.style.paddingRight = 8;
            page.style.paddingTop = 6;
            page.style.paddingBottom = 8;
            page.style.flexDirection = FlexDirection.Column;
            page.style.flexGrow = 1;
            page.style.minHeight = 0;
            page.style.minWidth = 0;
            // Make the complete Session workspace vertically scrollable. The
            // grid gets its own stable height, independent of the FX drawer.
            // Expanding FX below it therefore cannot crush the launch rows.
            contentScroll = new ScrollView(ScrollViewMode.Vertical);
            contentScroll.name = "ZPunkSessionPageScroll";
            contentScroll.style.flexGrow = 1;
            contentScroll.style.flexShrink = 1;
            contentScroll.style.minHeight = 0;
            contentScroll.style.minWidth = 0;
            contentScroll.style.width = Length.Percent(100);
            contentScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            contentScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            contentScroll.contentContainer.style.flexDirection = FlexDirection.Column;
            contentScroll.contentContainer.style.flexShrink = 0;
            page.Add(contentScroll);
            // MASTER / SCOPE belongs exclusively to ARRANGE; Session uses
            // this space for performance controls and the clip grid.
            var intro = new VisualElement();
            intro.style.flexDirection = FlexDirection.Row;
            intro.style.alignItems = Align.Center;
            intro.style.justifyContent = Justify.SpaceBetween;
            intro.style.marginBottom = 7;
            quantizeButton = ActionButton("QUANT " + QuantizeNames[quantizeIndex],
                CycleQuantize, 89);
            quantizeButton.style.fontSize = 11;
            quantizeButton.style.color = Accent;
            quantizeButton.tooltip = "Launch quantization: 1 BAR, 1/2, 1/4, 1/8. " +
                "Changes affect newly queued launches only.";
            intro.Add(quantizeButton);
            reloadButton = ActionButton("RELOAD", ShowCurrentProject, 63);
            reloadButton.tooltip = "Refresh clip slots from ARRANGE when Session is stopped";
            intro.Add(reloadButton);
            contentScroll.Add(intro);
            status = Caption("TAP A CLIP OR A SCENE TO START", 12, Muted);
            status.style.height = 31;
            status.style.unityTextAlign = TextAnchor.MiddleLeft;
            status.style.whiteSpace = WhiteSpace.NoWrap;
            status.style.overflow = Overflow.Hidden;
            status.style.textOverflow = TextOverflow.Ellipsis;
            contentScroll.Add(status);
            clipButtons.Clear();
            stopButtons.Clear();
            muteButtons.Clear();
            soloButtons.Clear();
            playbackFills.Clear();
            playbackCaptions.Clear();
            if (scenes.Count == 0 || !scenes.Exists(scene =>
                Array.Exists(scene.patterns, pattern => pattern != null)))
            {
                var empty = Caption("NO ARRANGEMENT CLIPS YET.\nCREATE CLIPS, THEN TAP RELOAD.", 14, Muted);
                empty.style.whiteSpace = WhiteSpace.Normal;
                empty.style.marginTop = 28;
                contentScroll.Add(empty);
                AddPerformanceToggle();
                AddPerformanceDrawer();
                LayoutRebuilt?.Invoke();
                return;
            }
            var scroller = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            scroller.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroller.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            scroller.name = "ZPunkSessionGridScroll";
            // Exactly the same launch-grid viewport when FX is open or shut.
            // Beyond five scenes the grid scrolls internally; on short phones
            // the whole page also scrolls, keeping every control accessible.
            int visibleScenes = Math.Min(scenes.Count, VisibleSceneRows);
            int gridHeight = 35 + visibleScenes * (TileHeight + ColumnGap) +
                PlaybackHeight + ColumnGap + MixRowHeight + ColumnGap +
                TileHeight + ColumnGap + 6;
            scroller.style.height = gridHeight;
            scroller.style.minHeight = gridHeight;
            scroller.style.flexGrow = 0;
            scroller.style.flexShrink = 0;
            scroller.style.minWidth = 0;
            scroller.style.width = Length.Percent(100);
            scroller.contentContainer.style.flexDirection = FlexDirection.Column;
            contentScroll.Add(scroller);

            var headings = new VisualElement();
            headings.style.flexDirection = FlexDirection.Row;
            headings.style.height = 35;
            headings.style.flexShrink = 0;
            var sceneHead = Column(SceneWidth);
            var sceneLabel = Caption("SCENE", 11, Muted);
            sceneLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            sceneHead.Add(sceneLabel);
            headings.Add(sceneHead);
            for (int t = 0; t < initialSlots.Length; t++)
            {
                TrackData lane = source.tracks[t];
                string text = lane == null ? "TRACK" : string.IsNullOrEmpty(lane.name)
                    ? lane.kind.ToString().ToUpperInvariant() : lane.name.ToUpperInvariant();
                var head = Column(TrackWidth);
                var caption = Caption(text, 11, TrackColor(t));
                caption.style.whiteSpace = WhiteSpace.NoWrap;
                caption.style.textOverflow = TextOverflow.Ellipsis;
                head.Add(caption);
                headings.Add(head);
                clipButtons[t] = new List<Button>();
            }
            var launchHead = Column(GoWidth);
            launchHead.Add(Caption("GO", 11, Accent));
            headings.Add(launchHead);
            scroller.Add(headings);

            for (int s = 0; s < scenes.Count; s++)
            {
                int scene = s;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.height = TileHeight + ColumnGap;
                row.style.flexShrink = 0;
                var sceneColumn = Column(SceneWidth);
                var sceneCell = new VisualElement();
                sceneCell.style.width = Length.Percent(100);
                sceneCell.style.height = TileHeight;
                sceneCell.style.flexDirection = FlexDirection.Row;
                sceneCell.style.alignItems = Align.Center;
                sceneCell.style.backgroundColor = new Color(.10f,.14f,.19f);
                sceneCell.style.borderTopLeftRadius = 5;
                sceneCell.style.borderBottomLeftRadius = 5;
                sceneCell.style.borderTopRightRadius = 5;
                sceneCell.style.borderBottomRightRadius = 5;
                var stripe = new VisualElement();
                stripe.style.width = 3;
                stripe.style.height = 36;
                stripe.style.marginLeft = 3;
                stripe.style.backgroundColor = Accent;
                stripe.pickingMode = PickingMode.Ignore;
                sceneCell.Add(stripe);
                var number = Caption((s + 1).ToString("00"), 13, Ink);
                number.style.flexGrow = 1;
                number.style.unityFontStyleAndWeight = FontStyle.Bold;
                number.tooltip = "ARRANGE bar " + (scenes[s].sourceTick / BarTicks + 1);
                sceneCell.Add(number);
                sceneColumn.Add(sceneCell);
                row.Add(sceneColumn);
                for (int t = 0; t < initialSlots.Length; t++)
                {
                    int track = t;
                    bool available = scenes[scene].patterns[track] != null;
                    string label = available ? "▶  " + (Math.Max(1,
                        (int)Math.Ceiling(scenes[scene].patterns[track].lengthTicks /
                            (double)BarTicks))).ToString() + "B" : "·";
                    var column = Column(TrackWidth);
                    var clipButton = ActionButton(label, () => QueueClip(track, scene), TrackWidth);
                    FillColumn(clipButton, TileHeight);
                    if (available) ZPunkUIIcons.Apply(clipButton, "play", label.Replace("▶", "").Trim(), 14f, false);
                    clipButton.SetEnabled(available);
                    clipButton.tooltip = available ? "Queue " + source.tracks[track].name +
                        " clip from arrangement bar " + (scenes[scene].sourceTick / BarTicks + 1)
                        : "No clip on this track in this scene";
                    column.Add(clipButton);
                    row.Add(column);
                    clipButtons[track].Add(clipButton);
                }
                var goColumn = Column(GoWidth);
                var go = ActionButton("", () => LaunchScene(scene), GoWidth);
                FillColumn(go, TileHeight);
                ZPunkUIIcons.Apply(go, "play", "", 19f);
                go.tooltip = "Queue all filled clips in scene " + (scene + 1) + "; empty cells keep playing";
                goColumn.Add(go);
                row.Add(goColumn);
                scroller.Add(row);
            }

            // Per-track Ableton-style clip position, directly above M/S.
            // This row has precisely the same Column() wrappers as the clips.
            var playback = new VisualElement();
            playback.style.flexDirection = FlexDirection.Row;
            playback.style.height = PlaybackHeight + ColumnGap;
            playback.style.flexShrink = 0;
            var playbackHead = Column(SceneWidth);
            playbackHead.Add(RowBadge("PLAY", PlaybackHeight));
            playback.Add(playbackHead);
            for (int t = 0; t < initialSlots.Length; t++)
            {
                var column = Column(TrackWidth);
                var cell = new VisualElement();
                cell.style.width = Length.Percent(100);
                cell.style.height = PlaybackHeight;
                cell.style.flexShrink = 0;
                cell.style.backgroundColor = new Color(.10f, .14f, .19f);
                cell.style.borderTopLeftRadius = 5;
                cell.style.borderTopRightRadius = 5;
                cell.style.borderBottomLeftRadius = 5;
                cell.style.borderBottomRightRadius = 5;
                cell.style.justifyContent = Justify.Center;
                var readout = Caption("—", 11, Muted);
                readout.style.height = 18;
                readout.style.unityFontStyleAndWeight = FontStyle.Bold;
                readout.tooltip = "Current clip's bar and progress through its loop";
                cell.Add(readout);
                var rail = new VisualElement();
                rail.style.height = 4;
                rail.style.marginTop = 1;
                rail.style.marginLeft = 7;
                rail.style.marginRight = 7;
                rail.style.backgroundColor = new Color(.18f, .23f, .29f);
                rail.style.borderTopLeftRadius = 2;
                rail.style.borderTopRightRadius = 2;
                rail.style.borderBottomLeftRadius = 2;
                rail.style.borderBottomRightRadius = 2;
                var fill = new VisualElement();
                fill.style.height = Length.Percent(100);
                fill.style.width = Length.Percent(0);
                fill.style.backgroundColor = TrackColor(t);
                fill.style.borderTopLeftRadius = 2;
                fill.style.borderTopRightRadius = 2;
                fill.style.borderBottomLeftRadius = 2;
                fill.style.borderBottomRightRadius = 2;
                fill.pickingMode = PickingMode.Ignore;
                rail.Add(fill);
                cell.Add(rail);
                column.Add(cell);
                playback.Add(column);
                playbackCaptions.Add(readout);
                playbackFills.Add(fill);
            }
            scroller.Add(playback);

            // Keep M and S stacked inside the *same* column wrapper as clips.
            var controls = new VisualElement();
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.height = MixRowHeight + ColumnGap;
            controls.style.flexShrink = 0;
            var mixHead = Column(SceneWidth);
            mixHead.Add(RowBadge("M / S", MixRowHeight));
            controls.Add(mixHead);
            for (int t = 0; t < initialSlots.Length; t++)
            {
                int track = t;
                var column = Column(TrackWidth);
                var mute = ActionButton("M", () => ToggleMute(track), TrackWidth);
                var solo = ActionButton("S", () => ToggleSolo(track), TrackWidth);
                FillColumn(mute, MixButtonHeight);
                FillColumn(solo, MixButtonHeight);
                solo.style.marginTop = MixButtonGap;
                muteButtons.Add(mute);
                soloButtons.Add(solo);
                column.Add(mute);
                column.Add(solo);
                controls.Add(column);
            }
            scroller.Add(controls);
            var stops = new VisualElement();
            stops.style.flexDirection = FlexDirection.Row;
            stops.style.height = TileHeight + ColumnGap;
            stops.style.flexShrink = 0;
            var stopHead = Column(SceneWidth);
            stopHead.Add(RowBadge("STOP", TileHeight));
            stops.Add(stopHead);
            for (int t = 0; t < initialSlots.Length; t++)
            {
                int track = t;
                var column = Column(TrackWidth);
                var button = ActionButton("", () => QueueClip(track, -1), TrackWidth);
                FillColumn(button, TileHeight);

                button.style.alignItems = Align.Center;
                button.style.justifyContent = Justify.Center;

                var stopIcon = new VisualElement();
                stopIcon.style.width = 13;
                stopIcon.style.height = 13;
                stopIcon.style.backgroundColor = Ink;
                stopIcon.pickingMode = PickingMode.Ignore;

                button.Add(stopIcon);
                button.tooltip = "Stop this lane at the next safe quantized boundary";
                column.Add(button);
                stops.Add(column);
                stopButtons.Add(button);
            }
            scroller.Add(stops);
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;
            footer.style.justifyContent = Justify.SpaceBetween;
            footer.style.marginTop = 8;
            footer.Add(Caption("EMPTY SLOTS KEEP PLAYING", 11, Muted));
            var stopAll = ActionButton("STOP ALL", StopAll, 92);
            stopAll.style.color = Accent;
            footer.Add(stopAll);
            contentScroll.Add(footer);
            AddPerformanceToggle();
            AddPerformanceDrawer();
            RefreshButtons();
            LayoutRebuilt?.Invoke();
        }

        // The toggle lives AFTER the complete grid and footer. Opening the
        // drawer adds content below this point, never above or over the grid.
        private void AddPerformanceToggle()
        {
            var button = ActionButton("", ToggleEffects, 100);
            button.name = "ZPunkSessionFxToggle";
            button.style.width = Length.Percent(100);
            button.style.minWidth = 0;
            button.style.marginTop = 10;
            button.style.marginBottom = 5;
            button.style.height = 42;
            button.style.fontSize = 13;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.color = Accent;
            button.style.backgroundColor = new Color(.10f, .17f, .20f);
            SetEffectsButtonIcon(button);
            button.tooltip = "Expand or collapse shared MUSIC / DRUMS filters and MASTER FX below the Session grid";
            contentScroll.Add(button);
        }

        private void SetEffectsButtonIcon(Button button)
        {
            if (button == null) return;
            // Rendered arrows, not font glyphs, for consistent iOS/Android UI.
            ZPunkUIIcons.Apply(button, effectsExpanded ? "down" : "right",
                "FILTERS / FX", 15f, false);
            var icon = button.Q<Image>("ZPunkIcon");
            if (icon != null) icon.tintColor = Accent;
            var caption = button.Q<Label>("ZPunkIconCaption");
            if (caption != null) caption.style.color = Accent;
            button.tooltip = "Expand or collapse the shared filters and FX below Session";
        }

        private void ToggleEffects()
        {
            effectsExpanded = !effectsExpanded;
            var drawer = page?.Q<VisualElement>("ZPunkSessionPerformanceDrawer");
            if (drawer != null) drawer.style.display = effectsExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            SetEffectsButtonIcon(page?.Q<Button>("ZPunkSessionFxToggle"));
        }

        private void AddPerformanceDrawer()
        {
            var drawer = new ScrollView(ScrollViewMode.Vertical) { name = "ZPunkSessionPerformanceDrawer" };
            drawer.style.flexShrink = 0;
            // Preserve enough launch-grid space even on short phones.
            // Both existing controls scroll inside this compact drawer.
            drawer.style.height = 360;
            drawer.style.minHeight = 0;
            drawer.style.width = Length.Percent(100);
            drawer.style.display = effectsExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            drawer.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            drawer.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            drawer.contentContainer.style.flexDirection = FlexDirection.Column;
            var host = new VisualElement { name = "ZPunkSessionPerformanceControlsHost" };
            host.style.flexDirection = FlexDirection.Column;
            host.style.width = Length.Percent(100);
            host.style.minWidth = 0;
            host.style.flexShrink = 0;
            drawer.Add(host);
            if (contentScroll != null) contentScroll.Add(drawer);
        }

        private void RefreshButtons()
        {
            if (source == null || status == null) return;
            bool isRunning = transport != null && transport.IsPlaying && transport.IsSessionArmed;
            // Reloading reparses the arrangement and would otherwise stop a live set.
            if (reloadButton != null) reloadButton.SetEnabled(!isRunning);
            long now = isRunning ? transport.CurrentTick : 0;
            long nearest = long.MaxValue;
            int pendingCount = 0;
            foreach (Launch launch in launches)
                if (launch.at > now)
                {
                    pendingCount++;
                    if (launch.at < nearest) nearest = launch.at;
                }
            if (transport != null && transport.IsPlaying && !transport.IsSessionArmed)
                status.text = "STOP ARRANGEMENT TO START SESSION";
            else if (isRunning && pendingCount > 0)
                status.text = "QUEUED " + pendingCount + "  /  BAR " + (nearest / BarTicks + 1);
            else status.text = isRunning ? "LIVE  /  QUANT " + QuantizeNames[quantizeIndex] :
                "TAP A CLIP OR A SCENE TO START";
            for (int t = 0; t < initialSlots.Length; t++)
            {
                int active = initialSlots[t];
                long mostRecent = -1;
                foreach (Launch launch in launches)
                    if (launch.track == t && launch.at <= now && launch.at > mostRecent)
                    {
                        mostRecent = launch.at;
                        active = launch.scene;
                    }
                // The active slot has a known launch tick and a source clip
                // length. Calculate loop phase from the same transport tick
                // used for the selected/queued clip colors, including after
                // 1/8-note quantized changes and the 64-bar cycle restart.
                if (t < playbackCaptions.Count && t < playbackFills.Count)
                {
                    Label readout = playbackCaptions[t];
                    VisualElement fill = playbackFills[t];
                    if (isRunning && active >= 0 && active < scenes.Count &&
                        scenes[active].patterns[t] != null)
                    {
                        long length = Math.Max(1L, scenes[active].patterns[t].lengthTicks);
                        long start = Math.Max(0L, mostRecent);
                        long phase = Math.Max(0L, now - start) % length;
                        int totalBars = Math.Max(1, (int)Math.Ceiling(length / (double)BarTicks));
                        int currentBar = Math.Min(totalBars, 1 + (int)(phase / BarTicks));
                        readout.text = currentBar.ToString("00") + " / " + totalBars.ToString("00");
                        readout.style.color = TrackColor(t);
                        fill.style.width = Length.Percent((float)(phase * 100.0 / length));
                    }
                    else
                    {
                        readout.text = "—";
                        readout.style.color = Muted;
                        fill.style.width = Length.Percent(0);
                    }
                }
                if (clipButtons.TryGetValue(t, out var buttons))
                    for (int s = 0; s < buttons.Count; s++)
                    {
                        bool selected = isRunning && s == active;
                        bool queued = false;
                        foreach (Launch launch in launches)
                            if (launch.track == t && launch.scene == s && launch.at > now)
                                queued = true;
                        Color color = TrackColor(t);
                        bool available = scenes[s].patterns[t] != null;
                        // Inactive clips get a subtle lane-colored tint. The
                        // currently sounding clip is filled with the actual track
                        // color; queued clips get a brighter intermediate tint.
                        Color background = selected ? color :
                            available ? Color.Lerp(Surface, color, queued ? .56f : .24f) : Surface;
                        buttons[s].style.backgroundColor = background;
                        buttons[s].style.color = selected ? Bg : available ? Ink : Muted;
                        var caption = buttons[s].Q<Label>("ZPunkIconCaption");
                        if (caption != null) caption.style.color = selected ? Bg : Ink;
                        var icon = buttons[s].Q<Image>("ZPunkIcon");
                        if (icon != null) icon.tintColor = selected ? Bg : Ink;
                    }
                if (t < muteButtons.Count)
                {
                    muteButtons[t].style.backgroundColor = muted[t] ? new Color(.35f,.18f,.22f) : Surface;
                    soloButtons[t].style.backgroundColor = soloed[t] ? new Color(.18f,.34f,.30f) : Surface;
                }
            }
        }

        private void Update()
        {
            if (!initialized || transport == null || page == null ||
                Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .12f;
            if (transport.IsPlaying && transport.IsSessionArmed)
            {
                // Keep selected slots across the performance loop without
                // ever writing the temporary clip grid to the project save.
                long tick = transport.CurrentTick;
                if (tick < previousTick && previousTick > BarTicks)
                {
                    for (int t = 0; t < initialSlots.Length; t++)
                        foreach (Launch launch in launches)
                            if (launch.track == t) initialSlots[t] = launch.scene;
                    launches.Clear();
                    transport.ApplySessionPlayback(BuildPerformanceSong());
                }
                previousTick = tick;
            }
            RefreshButtons();
        }
    }
}
