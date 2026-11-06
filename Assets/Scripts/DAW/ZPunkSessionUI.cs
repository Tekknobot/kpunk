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
        private VisualElement page;
        private Label status;
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
                long at = transport.NextSafeSessionBar();
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
                long at = transport.NextSafeSessionBar();
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
                long at = transport.NextSafeSessionBar();
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
            button.style.marginRight = 3;
            button.style.marginBottom = 3;
            button.style.borderTopLeftRadius = 5;
            button.style.borderTopRightRadius = 5;
            button.style.borderBottomLeftRadius = 5;
            button.style.borderBottomRightRadius = 5;
            return button;
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
            // Reserve the same inline MASTER / SCOPE module used by ARRANGE.
            // KMusicTabsUI moves the existing module here when SESSION is visible.
            var scopeHost = new VisualElement { name = "ZPunkSessionScopeHost" };
            scopeHost.style.flexDirection = FlexDirection.Column;
            scopeHost.style.flexShrink = 0;
            scopeHost.style.minWidth = 0;
            page.Add(scopeHost);
            var intro = new VisualElement();
            intro.style.flexDirection = FlexDirection.Row;
            intro.style.alignItems = Align.Center;
            intro.style.justifyContent = Justify.SpaceBetween;
            intro.style.marginBottom = 7;
            var title = Caption("LIVE CLIPS  /  1 BAR QUANTIZE", 12, Accent);
            title.style.unityTextAlign = TextAnchor.MiddleLeft;
            title.style.flexShrink = 1;
            intro.Add(title);
            var refresh = ActionButton("SYNC", ShowCurrentProject, 59);
            refresh.tooltip = "Reload clip slots from ARRANGE; stops the current session";
            intro.Add(refresh);
            page.Add(intro);
            status = Caption("TAP A CLIP OR A SCENE TO START", 12, Muted);
            status.style.height = 31;
            status.style.unityTextAlign = TextAnchor.MiddleLeft;
            status.style.whiteSpace = WhiteSpace.NoWrap;
            status.style.overflow = Overflow.Hidden;
            status.style.textOverflow = TextOverflow.Ellipsis;
            page.Add(status);
            clipButtons.Clear();
            stopButtons.Clear();
            muteButtons.Clear();
            soloButtons.Clear();
            if (scenes.Count == 0 || !scenes.Exists(scene =>
                Array.Exists(scene.patterns, pattern => pattern != null)))
            {
                var empty = Caption("NO ARRANGEMENT CLIPS YET.\nCREATE CLIPS, THEN TAP SYNC.", 14, Muted);
                empty.style.whiteSpace = WhiteSpace.Normal;
                empty.style.marginTop = 28;
                page.Add(empty);
                LayoutRebuilt?.Invoke();
                return;
            }
            var scroller = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            // Maintain touch/swipe scrolling without the native bright scrollbars.
            scroller.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroller.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            scroller.name = "ZPunkSessionGridScroll";
            scroller.style.flexGrow = 1;
            scroller.style.flexShrink = 1;
            scroller.style.minHeight = 0;
            scroller.style.minWidth = 0;
            scroller.style.width = new Length(100, LengthUnit.Percent);
            scroller.contentContainer.style.flexDirection = FlexDirection.Column;
            page.Add(scroller);
            var headings = new VisualElement();
            headings.style.flexDirection = FlexDirection.Row;
            headings.style.height = 35;
            var sceneLabel = Caption("SCENE", 11, Muted);
            sceneLabel.style.width = 61;
            sceneLabel.style.minWidth = 61;
            sceneLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            headings.Add(sceneLabel);
            for (int t = 0; t < initialSlots.Length; t++)
            {
                int track = t;
                TrackData lane = source.tracks[track];
                string text = lane == null ? "TRACK" : string.IsNullOrEmpty(lane.name)
                    ? lane.kind.ToString().ToUpperInvariant() : lane.name.ToUpperInvariant();
                var caption = Caption(text, 11, TrackColor(track));
                caption.style.width = 73;
                caption.style.minWidth = 73;
                caption.style.marginRight = 3;
                caption.style.whiteSpace = WhiteSpace.NoWrap;
                caption.style.textOverflow = TextOverflow.Ellipsis;
                headings.Add(caption);
                clipButtons[track] = new List<Button>();
            }
            var launchLabel = Caption("GO", 11, Accent);
            launchLabel.style.width = 45;
            headings.Add(launchLabel);
            scroller.Add(headings);
            for (int s = 0; s < scenes.Count; s++)
            {
                int scene = s;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.height = 57;
                // Scene tiles match the arrangement's tidy track-header treatment.
                var sceneCell = new VisualElement();
                sceneCell.style.width = 58;
                sceneCell.style.minWidth = 58;
                sceneCell.style.height = 51;
                sceneCell.style.marginRight = 3;
                sceneCell.style.marginBottom = 3;
                sceneCell.style.flexDirection = FlexDirection.Row;
                sceneCell.style.alignItems = Align.Center;
                sceneCell.style.backgroundColor = new Color(.10f,.14f,.19f);
                sceneCell.style.borderTopLeftRadius = 5;
                sceneCell.style.borderBottomLeftRadius = 5;
                sceneCell.style.borderTopRightRadius = 5;
                sceneCell.style.borderBottomRightRadius = 5;
                var sceneStripe = new VisualElement();
                sceneStripe.style.width = 3;
                sceneStripe.style.height = 35;
                sceneStripe.style.marginLeft = 2;
                sceneStripe.style.backgroundColor = Accent;
                sceneStripe.pickingMode = PickingMode.Ignore;
                sceneCell.Add(sceneStripe);
                var rowLabel = Caption((s + 1).ToString("00"), 13, Ink);
                rowLabel.style.flexGrow = 1;
                rowLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                rowLabel.tooltip = "ARRANGE bar " + (scenes[s].sourceTick / BarTicks + 1);
                sceneCell.Add(rowLabel);
                row.Add(sceneCell);
                for (int t = 0; t < initialSlots.Length; t++)
                {
                    int track = t;
                    bool available = scenes[scene].patterns[track] != null;
                    string label = available ? "▶  " + (Math.Max(1,
                        (int)Math.Ceiling(scenes[scene].patterns[track].lengthTicks /
                            (double)BarTicks))).ToString() + "B" : "·";
                    var clipButton = ActionButton(label, () => QueueClip(track, scene), 73);
                    clipButton.style.height = 51;
                    if (available) ZPunkUIIcons.Apply(clipButton, "play", label.Replace("▶", "").Trim(), 14f, false);
                    clipButton.SetEnabled(available);
                    clipButton.tooltip = available ? "Queue " + source.tracks[track].name +
                        " clip from arrangement bar " + (scenes[scene].sourceTick / BarTicks + 1)
                        : "No clip on this track in this scene";
                    row.Add(clipButton);
                    clipButtons[track].Add(clipButton);
                }
                var go = ActionButton("", () => LaunchScene(scene), 45);
                go.style.height = 51;
                ZPunkUIIcons.Apply(go, "play", "", 19f);
                go.tooltip = "Queue all filled clips in scene " + (scene + 1) + "; empty cells keep playing";
                row.Add(go);
                scroller.Add(row);
            }
            // M and S are stacked, giving every lane a single full-width tile
            // aligned exactly with the clip pads above (no cramped half-width row).
            var controls = new VisualElement();
            controls.style.flexDirection = FlexDirection.Row;
            controls.style.height = 59;
            var mixLabel = Caption("M / S", 10, Muted);
            mixLabel.style.width = 61;
            mixLabel.style.minWidth = 61;
            controls.Add(mixLabel);
            for (int t = 0; t < initialSlots.Length; t++)
            {
                int track = t;
                var group = new VisualElement();
                group.style.flexDirection = FlexDirection.Column;
                group.style.width = 73;
                group.style.minWidth = 73;
                group.style.marginRight = 3;
                var mute = ActionButton("M", () => ToggleMute(track), 73);
                var solo = ActionButton("S", () => ToggleSolo(track), 73);
                mute.style.height = solo.style.height = 26;
                mute.style.marginBottom = solo.style.marginBottom = 2;
                mute.style.marginRight = solo.style.marginRight = 0;
                muteButtons.Add(mute);
                soloButtons.Add(solo);
                group.Add(mute);
                group.Add(solo);
                controls.Add(group);
            }
            scroller.Add(controls);
            var stops = new VisualElement();
            stops.style.flexDirection = FlexDirection.Row;
            stops.style.height = 53;
            var stopLabel = Caption("STOP", 10, Muted);
            stopLabel.style.width = 61;
            stopLabel.style.minWidth = 61;
            stops.Add(stopLabel);
            for (int t = 0; t < initialSlots.Length; t++)
            {
                int track = t;
                var button = ActionButton("", () => QueueClip(track, -1), 73);
                button.style.height = 47;
                // The stock 'stop' icon is an outline. A solid square is more
                // recognizable and matches the transport's filled stop icon.
                button.text = "■";
                button.style.fontSize = 23;
                button.style.color = Ink;
                button.tooltip = "Stop this lane on the next bar";
                stops.Add(button);
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
            page.Add(footer);
            RefreshButtons();
            LayoutRebuilt?.Invoke();
        }

        private void RefreshButtons()
        {
            if (source == null || status == null) return;
            bool isRunning = transport != null && transport.IsPlaying && transport.IsSessionArmed;
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
            else status.text = isRunning ? "LIVE  /  NEXT BAR SYNC" :
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
                if (clipButtons.TryGetValue(t, out var buttons))
                    for (int s = 0; s < buttons.Count; s++)
                    {
                        bool selected = isRunning && s == active;
                        bool queued = false;
                        foreach (Launch launch in launches)
                            if (launch.track == t && launch.scene == s && launch.at > now)
                                queued = true;
                        Color color = TrackColor(t);
                        buttons[s].style.backgroundColor = selected ? new Color(
                            color.r * .43f, color.g * .43f, color.b * .43f) :
                            queued ? new Color(.21f,.30f,.34f) : Surface;
                        buttons[s].style.color = selected || queued ? Accent : color;
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
