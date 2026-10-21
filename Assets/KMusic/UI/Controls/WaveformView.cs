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

        private float[] _peaks;
        private int _peakCount;
        private AudioClip _peakClip;
        private IVisualElementScheduledItem _peakJob;
        private int _peakGeneration;
        // This separate 2px element moves without rebuilding the whole 2k-
        // segment vector waveform on every playback timer tick.
        private readonly VisualElement _playheadOverlay;

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
            // both boundaries. Zoom never exceeds the waveform's own 32x limit.
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

        public void ZoomIn() => ZoomAround(ViewStart01 + ViewLen01 * .5f, .5f);
        public void ZoomOut() => ZoomAround(ViewStart01 + ViewLen01 * .5f, 2f);
        public void RestoreWindow(float start, float length) => SetViewWindow(start, length);
        public float VisibleBars => _rulerSeconds * ViewLen01 * _rulerBpm / 240f;

        private bool _pointerDown;
        private Vector2 _pointerDownPosition;
        private bool _pointerMoved;
        private bool _pointerWasPinching;
        private Vector2 _lastPointerPosition;
        private Vector2 _pointerDownPanelPosition;
        private Vector2 _lastPointerPanelPosition;
        private bool _panningWave;
        private bool _verticalScrolling;
        private int _pointerIdPrimary = -1;
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
        private const float MinViewLen01 = 1f / 32f;   // max zoom-in ~32x
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

        private void MovePlayhead()
        {
            if (_playheadOverlay == null) return;
            bool visible = _playhead01 >= 0f &&
                _playhead01 >= ViewStart01 && _playhead01 <= ViewStart01 + ViewLen01;
            if (!visible)
            {
                _playheadOverlay.style.display = DisplayStyle.None;
                return;
            }
            float fraction = Mathf.Clamp01((_playhead01 - ViewStart01) / Mathf.Max(.000001f, ViewLen01));
            _playheadOverlay.style.display = DisplayStyle.Flex;
            _playheadOverlay.style.left = fraction * Mathf.Max(0f, contentRect.width - 2.5f);
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
            // Repeated sampler UI refreshes should not synchronously scan the
            // entire AudioClip (potentially millions of PCM frames) again.
            if (ReferenceEquals(_peakClip, clip) && _peaks != null) return;
            _peakGeneration++;
            if (_peakJob != null) { _peakJob.Pause(); _peakJob = null; }
            _peakClip = clip;
            _guides.Clear();
            _sliceStart01 = _sliceEnd01 = -1f;
            _peaks = null;
            _peakCount = 0;
            ResetZoom();
            if (clip == null || clip.samples <= 0) { MarkDirtyRepaint(); return; }

            int channels = Mathf.Max(1, clip.channels);
            _peakCount = Mathf.Min(2048, Mathf.Max(128, clip.samples));
            _peaks = new float[_peakCount * 2];
            float[] peaks = _peaks;
            int total = clip.samples;
            int buckets = _peakCount;
            const int frameBudget = 32768;
            float[] chunk = new float[Mathf.Min(frameBudget, total) * channels];
            int cursor = 0;
            int version = _peakGeneration;
            Action work = () =>
            {
                if (version != _peakGeneration || _peakClip != clip) return;
                int frames = Mathf.Min(frameBudget, total - cursor);
                if (frames <= 0) return;
                // Reuse one input buffer across every frame. At the last chunk
                // GetData may wrap, but only 'frames' valid samples are read.
                if (!clip.GetData(chunk, cursor))
                {
                    cursor = total;
                    return;
                }
                for (int frame = 0; frame < frames; ++frame)
                {
                    int bucket = Mathf.Min(buckets - 1,
                        (int)((long)(cursor + frame) * buckets / total));
                    float v = chunk[frame * channels];
                    int ix = bucket * 2;
                    peaks[ix] = Mathf.Min(peaks[ix], v);
                    peaks[ix + 1] = Mathf.Max(peaks[ix + 1], v);
                }
                cursor += frames;
            };
            // Short one-shots draw at once; songs and long chops are sampled
            // across UI frames so no audio deadline is blocked by waveform IO.
            if (total <= frameBudget * 2)
            {
                while (cursor < total) work();
                MarkDirtyRepaint();
            }
            else
            {
                _peakJob = schedule.Execute(() =>
                {
                    if (version != _peakGeneration || _peakClip != clip)
                    {
                        _peakJob?.Pause();
                        return;
                    }
                    work();
                    if (cursor >= total)
                    {
                        _peakJob?.Pause();
                        _peakJob = null;
                        MarkDirtyRepaint();
                    }
                    else if ((cursor / frameBudget) % 8 == 0)
                        MarkDirtyRepaint();
                }).Every(16);
            }
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

            // Waveform: draw only the visible window range
            if (_peaks != null && _peakCount > 0)
            {
                painter.lineWidth = 1.5f;
                painter.strokeColor = WaveColor;

                float midY = r.y + r.height * 0.5f;
                float halfH = r.height * 0.45f;

                // Map window into peak indices
                int i0 = Mathf.Clamp(Mathf.FloorToInt(v0 * (_peakCount - 1)), 0, _peakCount - 1);
                int i1 = Mathf.Clamp(Mathf.CeilToInt(v1 * (_peakCount - 1)), 0, _peakCount - 1);
                int span = Mathf.Max(1, i1 - i0);

                for (int i = 0; i <= span; i++)
                {
                    int idx = i0 + i;
                    if (idx < 0 || idx >= _peakCount) continue;

                    // Use each peak's absolute sample position. Mapping the
                    // first/last visible bins to the window edges made drums
                    // appear shifted against accurate transient markers when
                    // zooming into a chop (particularly with long songs).
                    float t01 = idx / (float)_peakCount;
                    float tWin = (t01 - v0) / vLen;
                    if (tWin < 0f || tWin > 1f) continue;
                    float px = r.x + tWin * r.width;

                    float min = _peaks[idx * 2];
                    float max = _peaks[idx * 2 + 1];

                    float y1p = midY + min * halfH;
                    float y2p = midY + max * halfH;

                    painter.BeginPath();
                    painter.MoveTo(new Vector2(px, y1p));
                    painter.LineTo(new Vector2(px, y2p));
                    painter.Stroke();
                }
            }

            // Subtle bar/beat grid shares the waveform's viewport transform.
            if (_rulerSeconds > 0f && _rulerBpm > 0f)
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
                _p1Pos = evt.localPosition;
            }
            else if (_p2 == -1 && evt.pointerId != _p1)
            {
                _p2 = evt.pointerId;
                _p2Pos = evt.localPosition;
                BeginPinch();
            }

            // If we are NOT pinching, treat first pointer as primary for scrub/tap/marker-drag.
            if (!_pinching)
            {
                _pointerDown = true;
                _pointerIdPrimary = evt.pointerId;
                _pointerDownPosition = (Vector2)evt.localPosition;
                _lastPointerPosition = _pointerDownPosition;
                _pointerDownPanelPosition = (Vector2)evt.position;
                _lastPointerPanelPosition = _pointerDownPanelPosition;
                _panningWave = false;
                _verticalScrolling = false;
                _pointerMoved = false;
                _pointerWasPinching = false;

                float t01 = PositionToTimeline01(evt.localPosition);
                int markerIndex = MarkersDraggable ? FindMarkerIndexNear(evt.localPosition) : -1;
                if (markerIndex >= 0)
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
                    UserPointer01?.Invoke(t01, false);
                }
            }

            evt.StopPropagation();
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (evt == null) return;

            // update pointer positions
            if (evt.pointerId == _p1) _p1Pos = evt.localPosition;
            if (evt.pointerId == _p2) _p2Pos = evt.localPosition;

            if (_pinching)
            {
                UpdatePinch();
                _pointerWasPinching = true;
                evt.StopPropagation();
                return;
            }

            if (_pointerDown && evt.pointerId == _pointerIdPrimary)
            {
                Vector2 pos = (Vector2)evt.localPosition;
                Vector2 panelPos = (Vector2)evt.position;
                Vector2 displacement = panelPos - _pointerDownPanelPosition;
                if (displacement.sqrMagnitude > 64f) _pointerMoved = true;

                if (_draggingMarker && _dragMarkerIndex >= 0)
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
                    else
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
                bool clickedEmpty = !_draggingMarker && !_pointerMoved && !_pointerWasPinching
                    && ((Vector2)evt.localPosition - _pointerDownPosition).sqrMagnitude <= 64f;
                if (clickedEmpty) UserTap01?.Invoke(PositionToTimeline01(evt.localPosition));
                if (_draggingMarker && _dragMarkerIndex >= 0 && _pointerMoved)
                {
                    float t01 = PositionToTimeline01(evt.localPosition);
                    MarkerDrag01?.Invoke(_dragMarkerIndex, t01, false);
                }

                _pointerDown = false;
                _pointerIdPrimary = -1;
                _draggingMarker = false;
                _dragMarkerIndex = -1;
                _panningWave = false;
                _verticalScrolling = false;
                // Preserve selected-marker highlight after releasing a tap.
                UpdateMarkerLabels();
                MarkDirtyRepaint();
            }
            if (this.HasPointerCapture(evt.pointerId)) this.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnPointerCancel(PointerCancelEvent evt)
        {
            _pointerDown = false;
            _pointerIdPrimary = -1;
            _draggingMarker = false;
            _dragMarkerIndex = -1;
            _panningWave = false;
            _verticalScrolling = false;
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
            _pinching = true;
            _pointerWasPinching = true;
            _pointerMoved = true;

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
            if (_rulerSeconds <= 0f || _rulerBpm <= 0f || contentRect.width < 10f) return;

            float totalBeats = _rulerSeconds * _rulerBpm / 60f;
            float phaseBeats = _beatPhaseSeconds * _rulerBpm / 60f;
            float start = ViewStart01 * totalBeats - phaseBeats;
            float end = (ViewStart01 + ViewLen01) * totalBeats - phaseBeats;
            float pixelsPerBar = 4f / Mathf.Max(.0001f, totalBeats * ViewLen01) * contentRect.width;
            int barStep = pixelsPerBar < 32f ? 16 : pixelsPerBar < 65f ? 4 : 1;
            int firstBar = Mathf.Max(0, Mathf.CeilToInt(start / 4f / barStep) * barStep);
            int labelIndex = 0;
            for (int bar = firstBar; bar * 4f <= end && labelIndex < 128; bar += barStep)
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
                item.text = (bar + 1).ToString();
                item.style.left = ((bar * 4f + phaseBeats) / totalBeats - ViewStart01) / ViewLen01 * contentRect.width + 3f;
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