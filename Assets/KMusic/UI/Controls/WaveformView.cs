// Assets/KMusic/UI/Controls/WaveformView.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace KMusic.UI
{
    public class WaveformView : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<WaveformView, UxmlTraits> { }
        public new class UxmlTraits : VisualElement.UxmlTraits { }

        // Full-resolution min/max envelope plus half-resolution summaries.
        // A screen pixel reads only a few bins at its appropriate zoom level.
        // Keeping min AND max preserves narrow drum transients and stereo attacks.
        private float[] _peaks;
        private readonly List<float[]> _peakMip = new();
        private int _peakCount;
        private float _peakLoaded01;
        private bool _peakReadFailed;
        // Reused drawing buffers prevent managed allocations while editing.
        private float[] _columnHighs = new float[0];
        private float[] _columnLows = new float[0];
        private AudioClip _peakClip;
        private IVisualElementScheduledItem _peakJob;
        private int _peakGeneration;
        // Move the cursor independently, without re-tessellating the waveform
        // on every playback timer tick.
        private readonly VisualElement _playheadOverlay;
        // A real touch target, not just a narrow 2.5px line. It is placed in
        // the ruler so dragging it never accidentally edits a chop boundary.
        private readonly Label _playheadHandle;
        private bool _playheadSnapped;
        // Only the HEAD handle (or a very narrow strip immediately below the
        // ruler) starts a playhead drag. The old 22px-wide line hitbox and
        // full-ruler capture intercepted ordinary zoomed waveform swipes.
        private const float PlayheadGrabPx = 6f;
        private const float PlayheadLineGrabBelowRulerPx = 8f;
        private const float PlayheadSnapPx = 18f;
        private const float PlayheadRulerHeight = 25f;
        // HIT SNAP selects the nearest visible kick/snare guide. GRID SNAP
        // instead uses the musical sixteenth grid. Neither edits audio.
        public bool PlayheadSnapsToGuides { get; set; } = true;

        private readonly List<float> _markers01 = new();
        private readonly List<ZPunk.DAW.ZPunkSampleWarp.TransientHint> _guides = new();
        private float _sliceStart01 = -1f, _sliceEnd01 = -1f;

        public void SetTransientGuides(IEnumerable<ZPunk.DAW.ZPunkSampleWarp.TransientHint> guides)
        {
            _guides.Clear();
            if (guides != null) _guides.AddRange(guides);
            MarkDirtyRepaint();
        }

        public void SetActiveSlice01(float start01, float end01)
        {
            _sliceStart01 = end01 > start01 ? Mathf.Clamp01(start01) : -1f;
            _sliceEnd01 = _sliceStart01 >= 0f ? Mathf.Clamp01(end01) : -1f;
            MarkDirtyRepaint();
        }

        public void FocusSlice01(float start01, float end01)
        {
            if (end01 <= start01) return;
            // Frame the complete segment with enough surrounding audio to grab
            // both boundaries. The visible window is bounded by the 256x limit.
            float padding = Mathf.Max(.002f, (end01 - start01) * .12f);
            RestoreWindow(start01 - padding, end01 - start01 + padding * 2f);
        }

        // -1 means "hidden". (So we don't draw a confusing default line at t=0.)
        private float _playhead01 = -1f;

        public Color WaveColor = new(0.90f, 0.92f, 0.95f, 1f);
        public Color MarkerColor = new(0.42f, 0.89f, 1f, 1f);
        public Color PlayheadColor = new(1f, 0.36f, 0.39f, 1f);

        /// <summary>
        /// Fired when the user interacts with the waveform using ONE finger/mouse.
        /// t01 is absolute timeline normalized [0..1].
        /// isDrag true on move while pressed.
        /// </summary>
        public event Action<float, bool> UserPointer01;
        // Dedicated transport cursor interaction, separate from chop markers.
        public event Action<float> PlayheadMoved01;
        public event Action<bool> PlayheadGrabbed;
        public event Action<float> UserTap01;
        public event Action<int, float, bool> MarkerDrag01;
        // A marker tap selects it without moving it. The sampler can then delete
        // or nudge that exact marker instead of guessing from the playhead.
        public event Action<int> MarkerSelected;
        // When dragging vertically over the waveform, let the surrounding
        // sampler page scroll even though the waveform owns pointer capture.
        public event Action<float> VerticalDragDelta;

        private readonly VisualElement _markerLabelLayer;
        private readonly List<Label> _markerLabels = new();
        private readonly VisualElement _rulerLabelLayer;
        private readonly List<Label> _rulerLabels = new();
        private float _rulerBpm = 120f;
        private float _rulerSeconds;
        private float _beatPhaseSeconds;
        private bool _showMusicalGrid = true;
        public bool ShowMusicalGrid
        {
            get => _showMusicalGrid;
            set
            {
                if (_showMusicalGrid == value) return;
                _showMusicalGrid = value;
                UpdateRulerLabels();
                MarkDirtyRepaint();
            }
        }

        // Waveform ruler uses true musical beats at the active tempo. The viewport
        // clips bars naturally when zooming and panning; slices remain normalized.
        public void SetMusicalRuler(float bpm, float seconds, float phaseSeconds = 0f)
        {
            _rulerBpm = Mathf.Max(1f, bpm);
            _rulerSeconds = Mathf.Max(0f, seconds);
            _beatPhaseSeconds = Mathf.Max(0f, phaseSeconds);
            UpdateMarkerLabels();
            MarkDirtyRepaint();
        }

        // The explicit +/- zoom controls follow the sample playhead / HEAD
        // marker. Pinch-to-zoom and mouse-wheel zoom retain finger/cursor anchors.
        private float ButtonZoomAnchor => _playhead01 >= 0f
            ? _playhead01 : ViewStart01 + ViewLen01 * .5f;
        public void ZoomIn() => ZoomAround(ButtonZoomAnchor, .5f);
        public void ZoomOut() => ZoomAround(ButtonZoomAnchor, 2f);
        public void RestoreWindow(float start, float length) => SetViewWindow(start, length);
        public float VisibleBars => _rulerSeconds * ViewLen01 * _rulerBpm / 240f;

        private bool _pointerDown;
        private Vector2 _pointerDownPosition;
        private bool _pointerMoved;
        private bool _pointerWasPinching;
        // Empty ruler taps seek, but ruler swipes remain available for panning.
        private bool _rulerTapCandidate;
        private Vector2 _lastPointerPosition;
        private Vector2 _pointerDownPanelPosition;
        private Vector2 _lastPointerPanelPosition;
        private bool _panningWave;
        private bool _verticalScrolling;
        private int _pointerIdPrimary = -1;
        private bool _draggingPlayhead;
        private bool _draggingMarker;
        private int _dragMarkerIndex = -1;
        private int _activeMarkerIndex = -1;
        private const float MarkerGrabPx = 18f;

        public bool MarkersDraggable { get; set; } = false;
        public void SetSelectedMarker(int index)
        {
            _activeMarkerIndex = index >= 0 && index < _markers01.Count ? index : -1;
            UpdateMarkerLabels();
            MarkDirtyRepaint();
        }

        // --- Zoom window in timeline-normalized space ---
        // View maps [ViewStart01 .. ViewStart01+ViewLen01] to the control width.
        public float ViewStart01 { get; private set; } = 0f;
        public float ViewLen01 { get; private set; } = 1f;

        // Limit zoom (smaller = more zoom-in)
        private const float MinViewLen01 = 1f / 256f;  // max zoom-in ~256x
        private const float MaxViewLen01 = 1f;         // zoomed out = full track

        // --- Pinch/pan (2 pointers) ---
        private bool _pinching;
        private int _p1 = -1, _p2 = -1;
        private Vector2 _p1Pos, _p2Pos;
        private float _pinchStartDist;
        private float _pinchStartViewLen;
        private float _pinchStartViewStart;
        private float _pinchAnchorTimeline01; // the timeline position under pinch midpoint at pinch start
        private Vector2 _pinchStartMid;

        public WaveformView()
        {
            style.flexGrow = 1;
            style.height = 320;
            style.marginTop = 8;
            style.marginBottom = 8;

            pickingMode = PickingMode.Position;
            focusable = true;

            generateVisualContent += OnGenerate;

            _markerLabelLayer = new VisualElement();
            _markerLabelLayer.pickingMode = PickingMode.Ignore;
            _markerLabelLayer.style.position = Position.Absolute;
            _markerLabelLayer.style.left = 0;
            _markerLabelLayer.style.right = 0;
            _markerLabelLayer.style.top = 0;
            _markerLabelLayer.style.bottom = 0;
            Add(_markerLabelLayer);

            _rulerLabelLayer = new VisualElement();
            _rulerLabelLayer.pickingMode = PickingMode.Ignore;
            _rulerLabelLayer.style.position = Position.Absolute;
            _rulerLabelLayer.style.left = 0;
            _rulerLabelLayer.style.right = 0;
            _rulerLabelLayer.style.top = 0;
            _rulerLabelLayer.style.height = 22;
            Add(_rulerLabelLayer);

            _playheadOverlay = new VisualElement { name = "ZPunkWavePlayhead" };
            _playheadOverlay.pickingMode = PickingMode.Ignore;
            _playheadOverlay.style.position = Position.Absolute;
            _playheadOverlay.style.top = 0;
            _playheadOverlay.style.bottom = 0;
            _playheadOverlay.style.width = 2.5f;
            _playheadOverlay.style.backgroundColor = PlayheadColor;
            _playheadOverlay.style.display = DisplayStyle.None;
            Add(_playheadOverlay);

            _playheadHandle = new Label("HEAD") { name = "ZPunkPlayheadHandle" };
            // A genuine pickable touch target. The parent WaveformView still
            // owns capture and dispatches scrubbing; marker taps are unaffected.
            _playheadHandle.pickingMode = PickingMode.Position;
            _playheadHandle.style.position = Position.Absolute;
            _playheadHandle.style.top = 1f;
            _playheadHandle.style.width = 44f;
            _playheadHandle.style.height = 24f;
            _playheadHandle.style.fontSize = 10f;
            _playheadHandle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _playheadHandle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _playheadHandle.style.color = Color.white;
            _playheadHandle.style.backgroundColor = PlayheadColor;
            _playheadHandle.style.borderTopLeftRadius = 4f;
            _playheadHandle.style.borderTopRightRadius = 4f;
            _playheadHandle.style.borderBottomLeftRadius = 4f;
            _playheadHandle.style.borderBottomRightRadius = 4f;
            _playheadHandle.style.display = DisplayStyle.None;
            Add(_playheadHandle);

            RegisterCallback<GeometryChangedEvent>(_ => { UpdateMarkerLabels(); MovePlayhead(); });
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCancelEvent>(OnPointerCancel);

            // Editor convenience: mouse wheel zoom
            RegisterCallback<WheelEvent>(OnWheel);
        }

        /// <summary>
        /// Set playhead position in absolute normalized timeline [0..1].
        /// Pass a negative value (e.g. -1) to hide the playhead.
        /// </summary>
        public void SetPlayhead01(float t01)
        {
            float next = (t01 < 0f) ? -1f : Mathf.Clamp01(t01);
            if (Mathf.Abs(_playhead01 - next) < .00005f) return;
            _playhead01 = next;
            MovePlayhead();
        }

        // Track playback only when the cursor approaches the viewport edge.
        // Finger panning/pinch gestures retain full control of the viewport.
        public void FollowPlayhead(float t01)
        {
            if (_pointerDown || _pinching || ViewLen01 >= .9999f ||
                contentRect.width <= 2f || t01 < 0f) return;
            float screen = (t01 - ViewStart01) / ViewLen01;
            if (screen > .82f)
                SetViewWindow(t01 - ViewLen01 * .72f, ViewLen01);
            else if (screen < .12f)
                SetViewWindow(t01 - ViewLen01 * .22f, ViewLen01);
        }

        // During ruler/HEAD dragging, keep the head underneath the finger
        // while scrolling the zoomed waveform into view at either edge.
        // Called by the sampler's existing 30 Hz UI scheduler so it also works
        // when the finger pauses at the edge (no PointerMove events).
        public void AutoScrollDraggedPlayhead(float deltaTime)
        {
            if (!_draggingPlayhead || !_pointerDown || _pinching ||
                ViewLen01 >= .9999f || contentRect.width <= 2f) return;
            float x = _lastPointerPosition.x - contentRect.x;
            float width = contentRect.width;
            const float edge = 34f;
            float proximity = x < edge ? -(edge - x) / edge :
                x > width - edge ? (x - (width - edge)) / edge : 0f;
            if (Mathf.Abs(proximity) < .001f) return;
            float previousStart = ViewStart01;
            SetViewWindow(ViewStart01 + Mathf.Clamp(proximity, -2f, 2f) *
                ViewLen01 * Mathf.Min(.10f, Mathf.Max(0f, deltaTime)) * 1.35f,
                ViewLen01);
            if (Mathf.Abs(previousStart - ViewStart01) > .0000001f)
                DragPlayhead(_lastPointerPosition);
        }

        private void MovePlayhead()
        {
            if (_playheadOverlay == null) return;
            bool visible = _playhead01 >= 0f &&
                _playhead01 >= ViewStart01 && _playhead01 <= ViewStart01 + ViewLen01;
            if (!visible)
            {
                _playheadOverlay.style.display = DisplayStyle.None;
                _playheadHandle.style.display = DisplayStyle.None;
                return;
            }
            float fraction = Mathf.Clamp01((_playhead01 - ViewStart01) / Mathf.Max(.000001f, ViewLen01));
            float px = fraction * Mathf.Max(0f, contentRect.width);
            _playheadOverlay.style.display = DisplayStyle.Flex;
            _playheadOverlay.style.left = Mathf.Clamp(px - 1.25f, 0f, Mathf.Max(0f, contentRect.width - 2.5f));
            _playheadHandle.style.display = DisplayStyle.Flex;
            _playheadHandle.style.left = Mathf.Clamp(px - 22f, 0f, Mathf.Max(0f, contentRect.width - 44f));
            Color color = _playheadSnapped ? new Color(.40f, .86f, .73f, 1f) : PlayheadColor;
            _playheadOverlay.style.backgroundColor = color;
            _playheadHandle.style.backgroundColor = color;
        }

        public void SetMarkers01(IEnumerable<float> markers01)
        {
            _markers01.Clear();
            if (markers01 != null)
            {
                foreach (var m in markers01)
                {
                    // Starts live within the sample. The rightmost endpoint is
                    // represented as a visible closing marker for slice 16.
                    float t = Mathf.Clamp01(m);
                    // 0.0 is a valid, selectable chop start, not an implicit marker.
                    if (t >= 0.9995f) continue;
                    _markers01.Add(t);
                }
            }
            _markers01.Sort();
            // Slice 16 has a closing marker at the file end, even when the user
            // did not place a 17th point. Unlike another chop, it is a boundary.
            if (_markers01.Count == 16) _markers01.Add(1f);
            UpdateMarkerLabels();
            MarkDirtyRepaint();
        }

        public void ResetZoom()
        {
            ViewStart01 = 0f;
            ViewLen01 = 1f;
            UpdateMarkerLabels();
            MovePlayhead();
            MarkDirtyRepaint();
        }

        public void SetClip(AudioClip clip, int targetSamples = 2048)
        {
            // Do not rescan on player, layout or playback refresh. A new clip
            // (including a newly warped clip) starts a fresh, cancellable scan.
            if (ReferenceEquals(_peakClip, clip) && _peaks != null) return;
            _peakGeneration++;
            if (_peakJob != null) { _peakJob.Pause(); _peakJob = null; }
            _peakClip = clip;
            _guides.Clear();
            _sliceStart01 = _sliceEnd01 = -1f;
            _peaks = null;
            _peakMip.Clear();
            _peakCount = 0;
            _peakLoaded01 = 0f;
            _peakReadFailed = false;
            ResetZoom();
            if (clip == null || clip.samples <= 0) { MarkDirtyRepaint(); return; }

            // 32,768 source bins preserve roughly 1,024 bins at maximum 32x
            // zoom, instead of the ~64 bins visible with the old 2k cache.
            // Even long stereo recordings need only ~256 KiB for this level.
            int total = clip.samples;
            int channels = Mathf.Max(1, clip.channels);
            _peakCount = Mathf.Min(32768, total);
            _peaks = new float[_peakCount * 2];
            float[] peaks = _peaks;
            int buckets = _peakCount;
            const int framesPerChunk = 32768;
            float[] chunk = new float[Mathf.Min(framesPerChunk, total) * channels];
            int cursor = 0;
            int version = _peakGeneration;

            Action workChunk = () =>
            {
                int frames = Mathf.Min(framesPerChunk, total - cursor);
                if (frames <= 0) return;
                // GetData offsets are measured in sample FRAMES, not floats.
                // The final read may wrap within this reusable buffer, but we
                // only inspect the valid frame count.
                if (!clip.GetData(chunk, cursor))
                {
                    _peakReadFailed = true;
                    cursor = total;
                    return;
                }
                for (int frame = 0; frame < frames; ++frame)
                {
                    int index = (int)((long)(cursor + frame) * buckets / total) * 2;
                    int offset = frame * channels;
                    float low = chunk[offset];
                    float high = low;
                    for (int c = 1; c < channels; ++c)
                    {
                        float value = chunk[offset + c];
                        if (value < low) low = value;
                        if (value > high) high = value;
                    }
                    if (low < peaks[index]) peaks[index] = low;
                    if (high > peaks[index + 1]) peaks[index + 1] = high;
                }
                cursor += frames;
                _peakLoaded01 = cursor / (float)total;
            };

            Action finish = () =>
            {
                if (version != _peakGeneration || !ReferenceEquals(_peakClip, clip)) return;
                _peakJob?.Pause();
                _peakJob = null;
                if (!_peakReadFailed)
                {
                    // Build a min/max pyramid once. At full view a phone
                    // draws ~500 aggregate bins, not 32k individual strokes.
                    _peakMip.Add(peaks);
                    float[] level = peaks;
                    while (level.Length > 4)
                    {
                        int bins = level.Length / 2;
                        int nextBins = (bins + 1) / 2;
                        float[] next = new float[nextBins * 2];
                        for (int i = 0; i < nextBins; ++i)
                        {
                            int a = i * 4;
                            int b = Mathf.Min(a + 2, level.Length - 2);
                            next[i * 2] = Mathf.Min(level[a], level[b]);
                            next[i * 2 + 1] = Mathf.Max(level[a + 1], level[b + 1]);
                        }
                        _peakMip.Add(next);
                        level = next;
                    }
                }
                MarkDirtyRepaint();
            };

            // Small one-shots can render on their first frame. Larger samples
            // are decoded incrementally to keep scrolling and pads responsive.
            if (total <= framesPerChunk * 2)
            {
                while (cursor < total) workChunk();
                finish();
            }
            else
            {
                int ticks = 0;
                IVisualElementScheduledItem thisJob = null;
                thisJob = schedule.Execute(() =>
                {
                    if (version != _peakGeneration || !ReferenceEquals(_peakClip, clip))
                    {
                        // Never pause a newer clip's scheduled scan.
                        thisJob?.Pause();
                        return;
                    }
                    // Spend no more than ~3ms of a UI update reading peaks.
                    // More work is done on fast devices without penalizing slow
                    // phones, unlike a fixed 32k-frames-per-16ms scan.
                    long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
                        (long)(System.Diagnostics.Stopwatch.Frequency * .003);
                    int chunks = 0;
                    do
                    {
                        workChunk();
                        ++chunks;
                    }
                    while (cursor < total && chunks < 8 &&
                           System.Diagnostics.Stopwatch.GetTimestamp() < deadline);

                    if (cursor >= total) finish();
                    else if (++ticks % 4 == 0) MarkDirtyRepaint();
                }).Every(16);
                _peakJob = thisJob;
            }
        }

        // Project a visible pixel column through the peak pyramid. Retaining
        // both extrema produces crisp transients even at the full-song view.
        // All calculations stay in the same normalized clip coordinate system
        // as the HEAD cursor, chop boundaries and transient guides.
        private void GetColumnEnvelope(float start01, float end01, int levelIndex,
            out float minimum, out float maximum)
        {
            minimum = 0f;
            maximum = 0f;
            float[] level = _peakMip.Count > 0 ? _peakMip[levelIndex] : _peaks;
            if (level == null) return;
            int bins = level.Length / 2;
            int first = Mathf.Clamp(Mathf.FloorToInt(start01 * bins), 0, bins - 1);
            int last = Mathf.Clamp(Mathf.CeilToInt(end01 * bins) - 1, first, bins - 1);
            for (int b = first; b <= last; ++b)
            {
                int ix = b * 2;
                if (level[ix] < minimum) minimum = level[ix];
                if (level[ix + 1] > maximum) maximum = level[ix + 1];
            }
        }

        private void DrawWaveform(Painter2D painter, Rect rect, float start01, float length01)
        {
            if (_peaks == null || _peakCount <= 0 || _peakReadFailed) return;
            float end01 = Mathf.Min(start01 + length01, _peakLoaded01);
            if (end01 <= start01) return;

            int columns = Mathf.Clamp(Mathf.CeilToInt(rect.width), 2, 1536);
            int shownColumns = Mathf.Clamp(
                Mathf.CeilToInt((end01 - start01) / length01 * columns), 1, columns);
            float step = rect.width / columns;
            float midY = rect.y + rect.height * .5f;
            float gain = rect.height * .44f;

            // Pick a summary whose buckets are around the same width as a
            // displayed pixel. At high zoom use the un-decimated level.
            int levelIndex = 0;
            while (levelIndex + 1 < _peakMip.Count &&
                   (_peakMip[levelIndex].Length * .5f * length01 / columns) > 2f)
                ++levelIndex;

            // Sample the envelope once per screen pixel. Store output in arrays
            // so we can draw one filled silhouette and two fine outlines,
            // rather than thousands of individual strokes/meshes.
            if (_columnHighs.Length < shownColumns + 1)
            {
                _columnHighs = new float[shownColumns + 1];
                _columnLows = new float[shownColumns + 1];
            }
            float[] highs = _columnHighs;
            float[] lows = _columnLows;
            for (int x = 0; x <= shownColumns; ++x)
            {
                float a = start01 + (x / (float)columns) * length01;
                float b = Mathf.Min(_peakLoaded01, a + length01 / columns);
                GetColumnEnvelope(a, b, levelIndex, out float low, out float high);
                lows[x] = midY + low * gain;
                highs[x] = midY + high * gain;
            }

            painter.fillColor = new Color(.51f, .83f, .91f, .58f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.x, highs[0]));
            for (int x = 1; x <= shownColumns; ++x)
                painter.LineTo(new Vector2(rect.x + x * step, highs[x]));
            for (int x = shownColumns; x >= 0; --x)
                painter.LineTo(new Vector2(rect.x + x * step, lows[x]));
            painter.ClosePath();
            painter.Fill();

            painter.lineWidth = 1f;
            painter.strokeColor = new Color(.81f, .96f, 1f, .78f);
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.x, highs[0]));
            for (int x = 1; x <= shownColumns; ++x)
                painter.LineTo(new Vector2(rect.x + x * step, highs[x]));
            painter.Stroke();
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.x, lows[0]));
            for (int x = 1; x <= shownColumns; ++x)
                painter.LineTo(new Vector2(rect.x + x * step, lows[x]));
            painter.Stroke();
        }

        private void OnGenerate(MeshGenerationContext ctx)
        {
            var r = contentRect;
            if (r.width <= 2 || r.height <= 2) return;

            var painter = ctx.painter2D;

            float v0 = ViewStart01;
            float v1 = Mathf.Clamp01(ViewStart01 + ViewLen01);
            float vLen = Mathf.Max(1e-6f, v1 - v0);

            // The selected pad owns this shaded source interval. The entire
            // waveform remains available via FULL and ordinary pinch/pan.
            if (_sliceStart01 >= 0f && _sliceEnd01 > _sliceStart01 &&
                _sliceEnd01 >= v0 && _sliceStart01 <= v1)
            {
                float left = r.x + Mathf.Clamp01((_sliceStart01 - v0) / vLen) * r.width;
                float right = r.x + Mathf.Clamp01((_sliceEnd01 - v0) / vLen) * r.width;
                painter.fillColor = new Color(.20f, .78f, .64f, .17f);
                painter.BeginPath();
                painter.MoveTo(new Vector2(left, r.y + 21f));
                painter.LineTo(new Vector2(right, r.y + 21f));
                painter.LineTo(new Vector2(right, r.y + r.height));
                painter.LineTo(new Vector2(left, r.y + r.height));
                painter.ClosePath();
                painter.Fill();
            }

            // Peak pyramid + continuous envelope. No per-sample vector strokes.
            DrawWaveform(painter, r, v0, vLen);

            // Subtle bar/beat grid shares the waveform's viewport transform.
            if (_showMusicalGrid && _rulerSeconds > 0f && _rulerBpm > 0f)
            {
                float totalBeats = _rulerSeconds * _rulerBpm / 60f;
                float phaseBeats = _beatPhaseSeconds * _rulerBpm / 60f;
                float visibleBeats = totalBeats * vLen;
                int stepBeats = visibleBeats > 128f ? 16 : visibleBeats > 48f ? 4 : 1;
                int startBeat = Mathf.Max(0, Mathf.CeilToInt((v0 * totalBeats - phaseBeats) / stepBeats) * stepBeats);
                painter.lineWidth = 1f;
                for (int beat = startBeat; beat + phaseBeats < totalBeats * v1; beat += stepBeats)
                {
                    float x = r.x + ((beat + phaseBeats) / totalBeats - v0) / vLen * r.width;
                    painter.strokeColor = beat % 4 == 0
                        ? new Color(.25f, .49f, .57f, .50f) : new Color(.2f, .3f, .39f, .25f);
                    painter.BeginPath();
                    painter.MoveTo(new Vector2(x, r.y + 22));
                    painter.LineTo(new Vector2(x, r.y + r.height));
                    painter.Stroke();
                }
            }

            // Solid, restrained onset guides, separate from draggable chop markers.
            foreach (var guide in _guides)
            {
                if (guide.position01 < v0 || guide.position01 > v1) continue;
                float x = r.x + (guide.position01 - v0) / vLen * r.width;
                painter.strokeColor = guide.kick
                    ? new Color(.95f, .67f, .38f, .29f)
                    : new Color(.70f, .63f, .91f, .29f);
                painter.lineWidth = 1f;
                painter.BeginPath();
                painter.MoveTo(new Vector2(x, r.y + 24f));
                painter.LineTo(new Vector2(x, r.y + r.height));
                painter.Stroke();
            }

            // Markers (only those inside window)
            if (_markers01.Count > 0)
            {
                painter.lineWidth = 2f;
                painter.strokeColor = MarkerColor;

                for (int i = 0; i < _markers01.Count; i++)
                {
                    float m = _markers01[i];
                    if (m < v0 || m > v1) continue;
                    float tWin = (m - v0) / vLen;
                    float px = r.x + Mathf.Clamp01(tWin) * r.width;

                    painter.lineWidth = (i == _activeMarkerIndex) ? 4f : 2f;
                    painter.BeginPath();
                    painter.MoveTo(new Vector2(px, r.y));
                    painter.LineTo(new Vector2(px, r.y + r.height));
                    painter.Stroke();
                }
            }

            // Playhead is a lightweight overlay, not part of the waveform mesh.
        }

        // Snap in *visible pixels*, not in percent of the whole song. A 55ms
        // tolerance can be impossible to grab when a four-minute recording is
        // displayed on a phone. The 18px radius remains useful at any zoom.
        private float SnapPlayhead01(float candidate)
        {
            candidate = Mathf.Clamp01(candidate);
            float maxDelta01 = PlayheadSnapPx / Mathf.Max(1f, contentRect.width) * ViewLen01;
            float closest = maxDelta01;
            float snapped = candidate;
            bool found = false;
            if (PlayheadSnapsToGuides)
            {
                foreach (var hint in _guides)
                {
                    if (hint.position01 < ViewStart01 ||
                        hint.position01 > ViewStart01 + ViewLen01) continue;
                    float delta = Mathf.Abs(hint.position01 - candidate);
                    if (delta <= closest)
                    {
                        closest = delta;
                        snapped = hint.position01;
                        found = true;
                    }
                }
            }
            else if (_rulerSeconds > .001f && _rulerBpm > 1f)
            {
                float division = 15f / _rulerBpm; // sixteenth note
                float time = candidate * _rulerSeconds;
                float gridTime = _beatPhaseSeconds +
                    Mathf.Round((time - _beatPhaseSeconds) / division) * division;
                float grid01 = Mathf.Clamp01(gridTime / _rulerSeconds);
                if (Mathf.Abs(grid01 - candidate) <= closest)
                {
                    snapped = grid01;
                    found = true;
                }
            }
            _playheadSnapped = found;
            return Mathf.Clamp01(snapped);
        }

        private void DragPlayhead(Vector2 localPosition)
        {
            float target = SnapPlayhead01(PositionToTimeline01(localPosition));
            SetPlayhead01(target);
            MovePlayhead(); // refresh snap feedback even when target is unchanged
            // The player owns audio positioning and the Play button's start.
            PlayheadMoved01?.Invoke(target);
        }

        // --- Input ---
        private void OnPointerDown(PointerDownEvent evt)
        {
            if (evt == null) return;
            Focus();
            this.CapturePointer(evt.pointerId);

            // Track up to 2 pointers
            if (_p1 == -1)
            {
                _p1 = evt.pointerId;
                _p1Pos = this.WorldToLocal(evt.position);
            }
            else if (_p2 == -1 && evt.pointerId != _p1)
            {
                _p2 = evt.pointerId;
                _p2Pos = this.WorldToLocal(evt.position);
                BeginPinch();
            }

            // If we are NOT pinching, treat first pointer as primary for scrub/tap/marker-drag.
            if (!_pinching)
            {
                _pointerDown = true;
                _pointerIdPrimary = evt.pointerId;
                _pointerDownPosition = (Vector2)this.WorldToLocal(evt.position);
                _lastPointerPosition = _pointerDownPosition;
                _pointerDownPanelPosition = (Vector2)evt.position;
                _lastPointerPanelPosition = _pointerDownPanelPosition;
                _panningWave = false;
                _verticalScrolling = false;
                _pointerMoved = false;
                _pointerWasPinching = false;
                _rulerTapCandidate = false;

                Vector2 localPos = this.WorldToLocal(evt.position);
                bool inRuler = localPos.y >= 0f && localPos.y <= PlayheadRulerHeight;
                bool headVisible = _playhead01 >= ViewStart01 &&
                    _playhead01 <= ViewStart01 + ViewLen01;
                // Only HEAD itself is pickable across its full 44px width;
                // the remainder of the ruler no longer steals touch swipes.
                bool onHandle = headVisible &&
                    _playheadHandle.resolvedStyle.display == DisplayStyle.Flex &&
                    _playheadHandle.worldBound.Contains((Vector2)evt.position);
                int markerIndex = !inRuler && MarkersDraggable
                    ? FindMarkerIndexNear(localPos) : -1;
                float px = (_playhead01 - ViewStart01) /
                    Mathf.Max(.000001f, ViewLen01) * contentRect.width;
                bool onLineTip = headVisible && !inRuler &&
                    localPos.y <= PlayheadRulerHeight + PlayheadLineGrabBelowRulerPx &&
                    Mathf.Abs(localPos.x - px) <= PlayheadGrabPx;
                // Marker edits have priority below the ruler; HEAD remains
                // draggable using the visible handle or the narrow line tip.
                _draggingPlayhead = onHandle || (markerIndex < 0 && onLineTip);
                _rulerTapCandidate = inRuler && !_draggingPlayhead;
                if (_draggingPlayhead)
                {
                    _draggingMarker = false;
                    _dragMarkerIndex = -1;
                    _playheadHandle.text = "DRAG";
                    PlayheadGrabbed?.Invoke(true);
                    DragPlayhead(this.WorldToLocal(evt.position));
                }
                else if (markerIndex >= 0)
                {
                    _draggingMarker = true;
                    _dragMarkerIndex = markerIndex;
                    _activeMarkerIndex = markerIndex;
                    MarkerSelected?.Invoke(markerIndex);
                    UpdateMarkerLabels();
                    MarkDirtyRepaint();
                }
                else
                {
                    _draggingMarker = false;
                    _dragMarkerIndex = -1;
                    _activeMarkerIndex = -1;
                    UpdateMarkerLabels();
                    // Do not seek on pointer-down: a swipe should pan the
                    // zoomed waveform without first moving the playback head.
                    // A stationary tap will seek on pointer-up instead.
                }
            }

            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (evt == null) return;

            // update pointer positions
            if (evt.pointerId == _p1) _p1Pos = this.WorldToLocal(evt.position);
            if (evt.pointerId == _p2) _p2Pos = this.WorldToLocal(evt.position);

            if (_pinching)
            {
                UpdatePinch();
                _pointerWasPinching = true;
                evt.StopPropagation();
                return;
            }

            if (_pointerDown && evt.pointerId == _pointerIdPrimary)
            {
                Vector2 pos = (Vector2)this.WorldToLocal(evt.position);
                Vector2 panelPos = (Vector2)evt.position;
                Vector2 displacement = panelPos - _pointerDownPanelPosition;
                if (displacement.sqrMagnitude > 64f) _pointerMoved = true;

                if (_draggingPlayhead)
                {
                    // Dragging the cursor takes priority over pan/scroll,
                    // including while the view is zoomed into one chop.
                    DragPlayhead(pos);
                }
                else if (_draggingMarker && _dragMarkerIndex >= 0)
                {
                    if (_pointerMoved)
                        MarkerDrag01?.Invoke(_dragMarkerIndex, PositionToTimeline01(pos), true);
                }
                else if (_pointerMoved)
                {
                    // Direction-lock the first meaningful drag. Vertical drags
                    // scroll the sampler; horizontal drags pan zoomed audio.
                    if (!_panningWave && !_verticalScrolling)
                    {
                        if (Mathf.Abs(displacement.y) > Mathf.Abs(displacement.x))
                            _verticalScrolling = true;
                        else if (ViewLen01 < .9999f)
                            _panningWave = true;
                    }
                    if (_verticalScrolling)
                        VerticalDragDelta?.Invoke(panelPos.y - _lastPointerPanelPosition.y);
                    else if (_panningWave)
                    {
                        float width = Mathf.Max(1f, contentRect.width);
                        SetViewWindow(ViewStart01 - (panelPos.x - _lastPointerPanelPosition.x) / width * ViewLen01, ViewLen01);
                    }
                    else if (!_rulerTapCandidate)
                        UserPointer01?.Invoke(PositionToTimeline01(pos), true);
                }
                _lastPointerPosition = pos;
                _lastPointerPanelPosition = panelPos;
                evt.StopPropagation();
            }
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (evt == null) return;

            if (evt.pointerId == _p1) _p1 = -1;
            if (evt.pointerId == _p2) _p2 = -1;

            if (_pinching && (_p1 == -1 || _p2 == -1))
                EndPinch();

            if (evt.pointerId == _pointerIdPrimary)
            {
                if (_draggingPlayhead && _pointerDown && !_pointerWasPinching)
                {
                    DragPlayhead(this.WorldToLocal(evt.position));
                    PlayheadGrabbed?.Invoke(false);
                    _playheadHandle.text = "HEAD";
                }
                bool clickedEmpty = _pointerDown && !_draggingPlayhead && !_draggingMarker &&
                    !_pointerMoved && !_pointerWasPinching &&
                    ((Vector2)this.WorldToLocal(evt.position) - _pointerDownPosition).sqrMagnitude <= 64f;
                if (clickedEmpty)
                {
                    Vector2 localPos = this.WorldToLocal(evt.position);
                    if (_rulerTapCandidate)
                    {
                        // A tap on the ruler seeks; a swipe never grabs HEAD.
                        DragPlayhead(localPos);
                    }
                    else
                    {
                        float t01 = PositionToTimeline01(localPos);
                        UserPointer01?.Invoke(t01, false);
                        UserTap01?.Invoke(t01);
                    }
                }
                if (_pointerDown && _draggingMarker && _dragMarkerIndex >= 0 &&
                    _pointerMoved && !_pointerWasPinching)
                {
                    float t01 = PositionToTimeline01(this.WorldToLocal(evt.position));
                    MarkerDrag01?.Invoke(_dragMarkerIndex, t01, false);
                }

                _pointerDown = false;
                _pointerIdPrimary = -1;
                _draggingMarker = false;
                _draggingPlayhead = false;
                _dragMarkerIndex = -1;
                _panningWave = false;
                _verticalScrolling = false;
                _rulerTapCandidate = false;
                // Preserve selected-marker highlight after releasing a tap.
                UpdateMarkerLabels();
                MarkDirtyRepaint();
            }
            if (this.HasPointerCapture(evt.pointerId)) this.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            if (_draggingPlayhead) PlayheadGrabbed?.Invoke(false);
            _playheadHandle.text = "HEAD";
            _draggingPlayhead = false;
            _pointerDown = false;
            _pointerIdPrimary = -1;
            _draggingMarker = false;
            _dragMarkerIndex = -1;
            _panningWave = false;
            _verticalScrolling = false;
            _rulerTapCandidate = false;
            UpdateMarkerLabels();
            if (this.HasPointerCapture(evt.pointerId)) this.ReleasePointer(evt.pointerId);

            _p1 = -1;
            _p2 = -1;
            EndPinch();
        }

        private void OnWheel(WheelEvent evt)
        {
            // Editor only: zoom under mouse
            if (evt == null) return;

            float mouseTimeline01 = PositionToTimeline01(evt.localMousePosition);
            float dir = Mathf.Sign(evt.delta.y); // wheel down positive usually
            float factor = (dir > 0f) ? 1.12f : 0.89f; // down = zoom out, up = zoom in

            ZoomAround(mouseTimeline01, factor);
            evt.StopPropagation();
        }

        private void BeginPinch()
        {
            if (_draggingPlayhead) PlayheadGrabbed?.Invoke(false);
            _playheadHandle.text = "HEAD";
            _draggingPlayhead = false;
            _draggingMarker = false;
            _dragMarkerIndex = -1;
            _pointerDown = false; // suppress single-finger seek/tap after pinch
            _pinching = true;
            _pointerWasPinching = true;
            _pointerMoved = true;
            _rulerTapCandidate = false;

            _pinchStartDist = Vector2.Distance(_p1Pos, _p2Pos);
            _pinchStartViewLen = ViewLen01;
            _pinchStartViewStart = ViewStart01;

            _pinchStartMid = (_p1Pos + _p2Pos) * 0.5f;
            _pinchAnchorTimeline01 = PositionToTimeline01(_pinchStartMid);
        }

        private void UpdatePinch()
        {
            float dist = Vector2.Distance(_p1Pos, _p2Pos);
            if (_pinchStartDist <= 1e-3f) return;

            // pinch scale: dist larger -> zoom in (smaller view len)
            float scale = dist / _pinchStartDist;
            float newLen = _pinchStartViewLen / Mathf.Max(0.25f, scale);
            newLen = Mathf.Clamp(newLen, MinViewLen01, MaxViewLen01);

            // pan: midpoint delta shifts view window
            Vector2 mid = (_p1Pos + _p2Pos) * 0.5f;
            float dx = (mid.x - _pinchStartMid.x);

            // convert pixel delta to timeline delta based on current len
            float w = Mathf.Max(1f, contentRect.width);
            float panTimeline = -(dx / w) * newLen; // drag right pans window left (content follows fingers)

            // keep anchor stable while zooming
            float anchor = _pinchAnchorTimeline01;
            float start = anchor - newLen * ((anchor - _pinchStartViewStart) / Mathf.Max(1e-6f, _pinchStartViewLen));
            start += panTimeline;

            SetViewWindow(start, newLen);
        }

        private void EndPinch()
        {
            _pinching = false;
        }

        private void ZoomAround(float anchorTimeline01, float factor)
        {
            float newLen = Mathf.Clamp(ViewLen01 * factor, MinViewLen01, MaxViewLen01);

            // keep anchor under the same relative x
            float t = Mathf.InverseLerp(ViewStart01, ViewStart01 + ViewLen01, anchorTimeline01);
            float newStart = anchorTimeline01 - t * newLen;

            SetViewWindow(newStart, newLen);
        }

        private void SetViewWindow(float start01, float len01)
        {
            len01 = Mathf.Clamp(len01, MinViewLen01, MaxViewLen01);

            float maxStart = 1f - len01;
            start01 = Mathf.Clamp(start01, 0f, Mathf.Max(0f, maxStart));

            ViewStart01 = start01;
            ViewLen01 = len01;

            UpdateMarkerLabels();
            MovePlayhead();
            MarkDirtyRepaint();
        }


        private void UpdateMarkerLabels()
        {
            UpdateRulerLabels();
            if (_markerLabelLayer == null)
                return;

            while (_markerLabels.Count < _markers01.Count)
            {
                var lbl = new Label();
                lbl.pickingMode = PickingMode.Ignore;
                lbl.style.position = Position.Absolute;
                lbl.style.top = 24;
                lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
                lbl.style.fontSize = 12;
                lbl.style.color = MarkerColor;
                lbl.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
                lbl.style.paddingLeft = 4;
                lbl.style.paddingRight = 4;
                lbl.style.paddingTop = 1;
                lbl.style.paddingBottom = 1;
                lbl.style.borderTopLeftRadius = 4;
                lbl.style.borderTopRightRadius = 4;
                lbl.style.borderBottomLeftRadius = 4;
                lbl.style.borderBottomRightRadius = 4;
                _markerLabels.Add(lbl);
                _markerLabelLayer.Add(lbl);
            }

            var r = contentRect;
            float v0 = ViewStart01;
            float v1 = Mathf.Clamp01(ViewStart01 + ViewLen01);
            float vLen = Mathf.Max(1e-6f, v1 - v0);

            for (int i = 0; i < _markerLabels.Count; i++)
            {
                var lbl = _markerLabels[i];
                if (i >= _markers01.Count)
                {
                    lbl.style.display = DisplayStyle.None;
                    continue;
                }

                float m = _markers01[i];
                if (r.width <= 2f || m < v0 || m > v1)
                {
                    lbl.style.display = DisplayStyle.None;
                    continue;
                }

                float tWin = (m - v0) / vLen;
                float px = Mathf.Clamp01(tWin) * r.width;

                lbl.text = i == 16 && _markers01.Count >= 17 ? "END 16" : $"{(i + 1):00}";
                lbl.style.display = DisplayStyle.Flex;
                lbl.style.left = Mathf.Clamp(px - (i == 16 ? 42f : 12f), 0f,
                    Mathf.Max(0f, r.width - (i == 16 ? 55f : 30f)));
                lbl.style.color = (i == _activeMarkerIndex) ? Color.white : MarkerColor;
            }
        }

        private void UpdateRulerLabels()
        {
            if (_rulerLabelLayer == null) return;
            for (int i = 0; i < _rulerLabels.Count; ++i)
                _rulerLabels[i].style.display = DisplayStyle.None;
            if (!_showMusicalGrid || _rulerSeconds <= 0f || _rulerBpm <= 0f || contentRect.width < 10f) return;

            float totalBeats = _rulerSeconds * _rulerBpm / 60f;
            float phaseBeats = _beatPhaseSeconds * _rulerBpm / 60f;
            float start = ViewStart01 * totalBeats - phaseBeats;
            float end = (ViewStart01 + ViewLen01) * totalBeats - phaseBeats;
            float pixelsPerBeat = contentRect.width /
                Mathf.Max(.0001f, totalBeats * ViewLen01);
            int step = pixelsPerBeat >= 32f ? 1 :
                pixelsPerBeat >= 8f ? 4 : pixelsPerBeat >= 2f ? 16 : 64;
            int firstBeat = Mathf.Max(0, Mathf.CeilToInt(start / step) * step);
            int labelIndex = 0;
            for (int beat = firstBeat; beat <= end && labelIndex < 128; beat += step)
            {
                if (labelIndex >= _rulerLabels.Count)
                {
                    var label = new Label();
                    label.pickingMode = PickingMode.Ignore;
                    label.style.position = Position.Absolute;
                    label.style.top = 2;
                    label.style.fontSize = 10;
                    label.style.color = new Color(.67f, .82f, .87f);
                    _rulerLabelLayer.Add(label);
                    _rulerLabels.Add(label);
                }
                var item = _rulerLabels[labelIndex++];
                // At close zoom show the four beats of each bar. At wider
                // zoom label bars only, keeping the phone ruler uncluttered.
                item.text = step == 1 ?
                    ((beat / 4) + 1).ToString() + "." + ((beat % 4) + 1).ToString() :
                    ((beat / 4) + 1).ToString();
                item.style.left = ((beat + phaseBeats) / totalBeats - ViewStart01) /
                    ViewLen01 * contentRect.width + 3f;
                item.style.display = DisplayStyle.Flex;
            }
        }

        private int FindMarkerIndexNear(Vector2 localPos)
        {
            var r = contentRect;
            if (r.width <= 1f || _markers01.Count == 0)
                return -1;

            float v0 = ViewStart01;
            float v1 = Mathf.Clamp01(ViewStart01 + ViewLen01);
            float vLen = Mathf.Max(1e-6f, v1 - v0);
            float bestPx = MarkerGrabPx;
            int bestIndex = -1;

            for (int i = 0; i < _markers01.Count; i++)
            {
                float m = _markers01[i];
                if (m >= .9995f) continue; // implicit closing marker is not draggable
                if (m < v0 || m > v1) continue;

                float tWin = (m - v0) / vLen;
                float px = r.x + Mathf.Clamp01(tWin) * r.width;
                float d = Mathf.Abs(localPos.x - px);
                if (d <= bestPx)
                {
                    bestPx = d;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        // Convert local x into absolute timeline position based on view window.
        private float PositionToTimeline01(Vector2 localPos)
        {
            var r = contentRect;
            if (r.width <= 1f) return 0f;

            float x = Mathf.Clamp(localPos.x - r.x, 0f, r.width);
            float tWin = Mathf.Clamp01(x / r.width);

            return Mathf.Clamp01(ViewStart01 + tWin * ViewLen01);
        }
    }
}