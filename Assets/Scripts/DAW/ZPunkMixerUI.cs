using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic;

namespace ZPunk.DAW
{
    /// <summary>
    /// Touch-friendly vertical mixer banks. A held fader captures its pointer,
    /// and the vertical page is pinned for the duration of the adjustment.
    /// </summary>
    public sealed class ZPunkMixerUI : MonoBehaviour
    {
        private static readonly Color Bg = new Color(.073f, .101f, .146f);
        private static readonly Color Card = new Color(.108f, .147f, .204f);
        private static readonly Color Ink = new Color(.91f, .94f, .98f);
        private static readonly Color Dim = new Color(.58f, .67f, .76f);
        private static readonly Color Aqua = new Color(.30f, .87f, .75f);
        private static readonly Color Off = new Color(.18f, .23f, .30f);
        private static readonly Color Muted = new Color(.47f, .23f, .31f);
        private static readonly Color Solo = new Color(.45f, .38f, .20f);
        private VisualElement root, lanes;
        private ScrollView mixerScroll;
        private sealed class FaderGesture
        {
            public Action<Vector2> begin;
            public Action<Vector2> move;
            public Action<Vector2> end;
            public Action cancel;
        }
        private readonly Dictionary<VisualElement, FaderGesture> faderTargets =
            new Dictionary<VisualElement, FaderGesture>();
        private FaderGesture activeFader;
        private int activePointer = -1;
        private Vector2 lockedScroll;
        private float scrollLockUntil;
        private KMusicProjectManager projects;
        private KMusicApp app;
        private KMusicDrumSequencer drums;
        private ZPunkSongTransport transport;
        private int displayedSlot = -1;
        private const float RailTop = 14f;
        private const float RailHeight = 150f;
        private const float HandleHeight = 24f;
        private static readonly string[] DrumNames =
        {
            "KICK", "SNARE", "CLAP", "CLOSED", "OPEN", "RIDE", "RIM", "CRASH"
        };
        private static readonly Color[] TrackColors =
        {
            new Color(.98f, .66f, .28f), new Color(.96f, .36f, .48f),
            new Color(.35f, .60f, .98f), new Color(.66f, .45f, .94f),
            new Color(.24f, .82f, .69f)
        };

        public void Initialize(VisualElement host)
        {
            root = host;
            if (root == null) return;
            // Intercept fader pointers before the ScrollView's touch handler.
            // Empty/background swipes still scroll the mixer normally.
            root.UnregisterCallback<PointerDownEvent>(OnFaderDown, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerMoveEvent>(OnFaderMove, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerUpEvent>(OnFaderUp, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCancelEvent>(OnFaderCancel, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerCaptureOutEvent>(OnFaderCaptureLost);
            root.Clear(); faderTargets.Clear();
            activeFader = null; activePointer = -1;
            root.RegisterCallback<PointerDownEvent>(OnFaderDown, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerMoveEvent>(OnFaderMove, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(OnFaderUp, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCancelEvent>(OnFaderCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerCaptureOutEvent>(OnFaderCaptureLost);
            root.style.flexDirection = FlexDirection.Column;
            root.style.flexGrow = 1;
            root.style.minHeight = 0;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 9;
            root.style.backgroundColor = Bg;
            var heading = Text("MIXER  /  LEVELS", 16, Ink);
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.marginBottom = 3;
            root.Add(heading);
            var sub = Text("VERTICAL FADERS  /  LIVE AUDIO GAIN", 10, Dim);
            sub.style.marginBottom = 10;
            root.Add(sub);
            mixerScroll = new ScrollView(ScrollViewMode.Vertical);
            mixerScroll.style.flexGrow = 1;
            mixerScroll.style.minHeight = 0;
            mixerScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            mixerScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            lanes = mixerScroll.contentContainer;
            root.Add(mixerScroll);
            OnShown();
        }
        private void Resolve()
        {
            if (projects == null) projects = FindObjectOfType<KMusicProjectManager>();
            if (app == null) app = FindObjectOfType<KMusicApp>();
            if (drums == null) drums = FindObjectOfType<KMusicDrumSequencer>();
            if (transport == null) transport = FindObjectOfType<ZPunkSongTransport>();
        }
        private static Label Text(string content, int size, Color color)
        {
            var label = new Label(content);
            label.style.color = color;
            label.style.fontSize = Mathf.Max(12, size);
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.pickingMode = PickingMode.Ignore;
            return label;
        }
        private static void Round(VisualElement item, float radius)
        {
            item.style.borderTopLeftRadius = radius;
            item.style.borderTopRightRadius = radius;
            item.style.borderBottomLeftRadius = radius;
            item.style.borderBottomRightRadius = radius;
        }
        private static VisualElement Panel()
        {
            var panel = new VisualElement();
            panel.style.backgroundColor = Card;
            panel.style.paddingLeft = 9;
            panel.style.paddingRight = 9;
            panel.style.paddingTop = 10;
            panel.style.paddingBottom = 12;
            panel.style.marginBottom = 10;
            Round(panel, 7);
            return panel;
        }
        private static Button Switch(string label, Action action, bool enabled, bool isSolo = false)
        {
            var b = new Button(action) { text = label };
            b.style.height = 39;
            b.style.minWidth = 36;
            b.style.flexGrow = 1;
            b.style.flexShrink = 1;
            b.style.paddingLeft = 0;
            b.style.paddingRight = 0;
            b.style.fontSize = 12;
            b.style.color = Ink;
            b.style.backgroundColor = enabled ? (isSolo ? Solo : Muted) : Off;
            Round(b, 5);
            return b;
        }
        private static void SetSwitch(Button button, bool enabled, string offLabel, string onLabel, bool isSolo = false)
        {
            if (button == null) return;
            button.text = enabled ? onLabel : offLabel;
            button.style.backgroundColor = enabled ? (isSolo ? Solo : Muted) : Off;
        }
        private VisualElement Bank(string label, Color accent, bool drumsBank)
        {
            var panel = Panel();
            var title = Text(label, 12, accent);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 12;
            panel.Add(title);
            var grid = new VisualElement();
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.width = new Length(100, LengthUnit.Percent);
            panel.Add(grid);
            grid.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                float width = evt.newRect.width;
                if (width <= 1f) return;
                int cols;
                if (drumsBank)
                    cols = width >= 680f ? 8 : width >= 335f ? 4 : width >= 255f ? 3 : 2;
                else
                    cols = width >= 620f ? 5 : width >= 405f ? 4 : width >= 285f ? 3 : 2;
                // Percent sizes are recalculated only on viewport resizing, not while dragging.
                var percent = new Length(100f / cols, LengthUnit.Percent);
                foreach (var child in grid.Children()) child.style.width = percent;
            });
            lanes.Add(panel);
            return grid;
        }
        private void RestoreScroll()
        {
            if (mixerScroll != null) mixerScroll.scrollOffset = lockedScroll;
        }
        private void FinishDrag(int pointerId)
        {
            if (activePointer != pointerId) return;
            activeFader = null;
            activePointer = -1;
            scrollLockUntil = Time.unscaledTime + .25f;
            RestoreScroll();
        }
        private FaderGesture FindFader(VisualElement target)
        {
            while (target != null && target != root)
            {
                if (faderTargets.TryGetValue(target, out FaderGesture gesture)) return gesture;
                target = target.parent;
            }
            return null;
        }
        private void OnFaderDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || activeFader != null) return;
            var gesture = FindFader(evt.target as VisualElement);
            if (gesture == null) return;
            activeFader = gesture;
            activePointer = evt.pointerId;
            lockedScroll = mixerScroll != null ? mixerScroll.scrollOffset : Vector2.zero;
            root.CapturePointer(evt.pointerId);
            gesture.begin(evt.position);
            RestoreScroll();
            evt.StopPropagation();
        }
        private void OnFaderMove(PointerMoveEvent evt)
        {
            if (activeFader == null || activePointer != evt.pointerId) return;
            activeFader.move(evt.position);
            RestoreScroll();
            evt.StopPropagation();
        }
        private void OnFaderUp(PointerUpEvent evt)
        {
            if (activeFader == null || activePointer != evt.pointerId) return;
            activeFader.end(evt.position);
            FinishDrag(evt.pointerId);
            if (root.HasPointerCapture(evt.pointerId)) root.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }
        private void OnFaderCancel(PointerCancelEvent evt)
        {
            if (activeFader == null || activePointer != evt.pointerId) return;
            activeFader.cancel();
            FinishDrag(evt.pointerId);
            if (root.HasPointerCapture(evt.pointerId)) root.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }
        private void OnFaderCaptureLost(PointerCaptureOutEvent evt)
        {
            if (activeFader == null || activePointer != evt.pointerId) return;
            activeFader.cancel();
            FinishDrag(evt.pointerId);
        }
        /// <summary>Build one large, directly grabbable fader in a responsive bank.</summary>
        private VisualElement VerticalFader(string name, float initialValue, Color color,
            Action<float> onChange, bool throttleTrackWrites)
        {
            var cell = new VisualElement();
            cell.style.width = new Length(50, LengthUnit.Percent);
            cell.style.minWidth = 0;
            cell.style.height = 289;
            cell.style.alignItems = Align.Center;
            cell.style.paddingLeft = 2;
            cell.style.paddingRight = 2;
            cell.style.paddingBottom = 9;

            var nameLabel = Text(name, 11, color);
            nameLabel.style.height = 23;
            nameLabel.style.width = new Length(100, LengthUnit.Percent);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            cell.Add(nameLabel);

            var percent = Text("", 11, Ink);
            percent.style.height = 23;
            percent.style.unityTextAlign = TextAnchor.MiddleCenter;
            cell.Add(percent);

            // Entire 59 x 178 area is touchable, including the white handle.
            var hit = new VisualElement();
            hit.style.width = 59;
            hit.style.height = 178;
            hit.style.position = Position.Relative;
            hit.style.flexShrink = 0;
            hit.pickingMode = PickingMode.Position;
            cell.Add(hit);

            var rail = new VisualElement();
            rail.style.position = Position.Absolute;
            rail.style.left = 25;
            rail.style.top = RailTop;
            rail.style.width = 9;
            rail.style.height = RailHeight;
            rail.style.backgroundColor = new Color(.045f, .068f, .10f);
            Round(rail, 4);
            rail.pickingMode = PickingMode.Ignore;
            hit.Add(rail);

            var fill = new VisualElement();
            fill.style.position = Position.Absolute;
            fill.style.bottom = 0;
            fill.style.left = 2;
            fill.style.width = 5;
            fill.style.backgroundColor = color;
            Round(fill, 3);
            fill.pickingMode = PickingMode.Ignore;
            rail.Add(fill);

            var thumb = new VisualElement();
            thumb.style.position = Position.Absolute;
            thumb.style.left = 9;
            thumb.style.width = 41;
            thumb.style.height = HandleHeight;
            thumb.style.backgroundColor = Ink;
            Round(thumb, 6);
            thumb.pickingMode = PickingMode.Ignore;
            hit.Add(thumb);
            var notch = new VisualElement();
            notch.style.position = Position.Absolute;
            notch.style.left = 11;
            notch.style.right = 11;
            notch.style.top = 11;
            notch.style.height = 2;
            notch.style.backgroundColor = new Color(.26f, .34f, .40f);
            notch.pickingMode = PickingMode.Ignore;
            thumb.Add(notch);

            float value = Mathf.Clamp01(initialValue);
            float lastDelivered = value;
            float nextWrite = 0f;
            Action<float> show = v =>
            {
                value = Mathf.Clamp01(v);
                fill.style.height = new Length(value * 100f, LengthUnit.Percent);
                thumb.style.top = RailTop + RailHeight * (1f - value) - HandleHeight * .5f;
                percent.text = Mathf.RoundToInt(value * 100f) + "%";
            };
            Action<bool> publish = force =>
            {
                if (Mathf.Abs(value - lastDelivered) < .002f) return;
                if (!force && throttleTrackWrites && Time.unscaledTime < nextWrite) return;
                lastDelivered = value;
                nextWrite = Time.unscaledTime + .045f;
                onChange(value);
            };
            show(value);
            float grabOffset = 0f;
            Action<Vector2> setByFinger = position =>
            {
                float y = hit.WorldToLocal(position).y - grabOffset;
                show(1f - (y - RailTop) / RailHeight);
                publish(false);
            };
            faderTargets[hit] = new FaderGesture
            {
                begin = position =>
                {
                    float fingerY = hit.WorldToLocal(position).y;
                    float handleY = RailTop + RailHeight * (1f - value);
                    // Grabbing the handle keeps its distance to your finger.
                    // Tapping the empty rail jumps straight to that level.
                    grabOffset = Mathf.Abs(fingerY - handleY) <= 20f ? fingerY - handleY : 0f;
                    if (Mathf.Abs(fingerY - handleY) > 20f) setByFinger(position);
                },
                move = position => setByFinger(position),
                end = position => { setByFinger(position); publish(true); },
                cancel = () => publish(true)
            };
            // Buttons attach here; the fader itself owns no ScrollView gesture.
            var actions = new VisualElement();
            actions.name = "FaderActions";
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.justifyContent = Justify.Center;
            actions.style.width = new Length(100, LengthUnit.Percent);
            actions.style.paddingLeft = 2;
            actions.style.paddingRight = 2;
            actions.style.marginTop = 3;
            cell.Add(actions);
            return cell;
        }
        private void ChangeTrack(int index, Action<TrackData> edit, bool cutSustains = false)
        {
            Resolve();
            if (projects == null) return;
            var song = projects.GetSongForArrangementEditor();
            if (song == null || song.tracks == null || index < 0 || index >= song.tracks.Count) return;
            if (!ReferenceEquals(projects.CurrentSong, song)) song = ArrangementEditing.IndependentCopy(song);
            if (song == null || song.tracks[index] == null) return;
            edit(song.tracks[index]);
            projects.AcceptArrangementEdit(song);
            if (transport != null) transport.ApplyLiveMix(song, cutSustains);
        }
        public void OnShown()
        {
            if (root == null || lanes == null) return;
            Resolve();
            if (activePointer >= 0 && root.HasPointerCapture(activePointer))
                root.ReleasePointer(activePointer);
            activeFader = null; activePointer = -1;
            faderTargets.Clear();
            lanes.Clear();
            displayedSlot = projects != null ? projects.CurrentSlot : -1;
            var song = projects != null ? projects.GetSongForArrangementEditor() : null;
            if (song != null && song.tracks != null)
            {
                var tracksGrid = Bank("TRACK LEVELS", Aqua, false);
                for (int i = 0; i < song.tracks.Count; ++i)
                {
                    int index = i;
                    var track = song.tracks[i];
                    if (track == null) continue;
                    Color color = TrackColors[Mathf.Min(i, TrackColors.Length - 1)];
                    var fader = VerticalFader((track.name ?? track.kind.ToString()).ToUpperInvariant(),
                        track.volume, color, v => ChangeTrack(index, t => t.volume = v), true);
                    var actions = fader.Q<VisualElement>("FaderActions");
                    Button mute = null, solo = null;
                    mute = Switch("M", () =>
                    {
                        ChangeTrack(index, t => t.mute = !t.mute);
                        var changed = projects.CurrentSong.tracks[index];
                        SetSwitch(mute, changed.mute, "M", "M!");
                    }, track.mute);
                    solo = Switch("S", () =>
                    {
                        ChangeTrack(index, t => t.solo = !t.solo);
                        var changed = projects.CurrentSong.tracks[index];
                        SetSwitch(solo, changed.solo, "S", "S!", true);
                    }, track.solo, true);
                    SetSwitch(mute, track.mute, "M", "M!");
                    SetSwitch(solo, track.solo, "S", "S!", true);
                    mute.style.marginRight = 3;
                    actions.Add(mute);
                    actions.Add(solo);
                    tracksGrid.Add(fader);
                }
            }
            var drumGrid = Bank("DRUM CHANNELS  /  KIT 1-8", Aqua, true);
            if (app == null || app.Bus == null || drums == null)
            {
                drumGrid.Add(Text("Waiting for drum mixer and parameter bus.", 11, Dim));
                return;
            }
            bool[] mutes = drums.CaptureDrumMutes();
            for (int i = 0; i < 8; ++i)
            {
                int lane = i;
                string id = "drum.vol" + (i + 1).ToString("00");
                var color = new Color(.98f, .68f, .31f);
                var fader = VerticalFader(DrumNames[i], app.Bus.GetNormalized(id), color, v =>
                {
                    if (app != null && app.Bus != null) app.Bus.SetNormalized(id, v);
                }, false);
                var actions = fader.Q<VisualElement>("FaderActions");
                bool muted = i < mutes.Length && mutes[i];
                Button mute = null;
                mute = Switch("M", () =>
                {
                    bool[] state = drums.CaptureDrumMutes();
                    bool target = !(lane < state.Length && state[lane]);
                    drums.SetDAWDrumMute(lane, target);
                    SetSwitch(mute, target, "M", "M!");
                }, muted);
                SetSwitch(mute, muted, "M", "M!");
                mute.style.flexGrow = 0;
                mute.style.width = 46;
                actions.Add(mute);
                drumGrid.Add(fader);
            }
            var note = Text("Drag faders up or down. Tap M to mute, S to solo.", 10, Dim);
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.marginBottom = 14;
            lanes.Add(note);
        }
        private void Update()
        {
            if (root == null || root.resolvedStyle.display == DisplayStyle.None) return;
            if (activeFader != null || Time.unscaledTime < scrollLockUntil) RestoreScroll();
            if (projects != null && projects.CurrentSlot != displayedSlot) OnShown();
        }
    }
}
