using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace KMusic.UI
{
    /// <summary>
    /// Paintable piano roll grid:
    /// - rows = notes (each cell can have its own label + color)
    /// - cols = steps
    /// - click/drag paints ON; clicking an already-on cell toggles OFF
    /// - no lane selection needed (note comes from the row/cell you click)
    ///
    /// Special: If rows=6 and cols=8, we use an exact label+color layout
    /// matching the provided reference image (top-left to bottom-right).
    /// </summary>
    public class PianoRollGrid : VisualElement
    {
        public new class UxmlFactory : UxmlFactory<PianoRollGrid, UxmlTraits> { }

        public new class UxmlTraits : VisualElement.UxmlTraits
        {
            UxmlIntAttributeDescription _rows = new UxmlIntAttributeDescription { name = "rows", defaultValue = 6 };
            UxmlIntAttributeDescription _cols = new UxmlIntAttributeDescription { name = "cols", defaultValue = 8 };

            public override void Init(VisualElement ve, IUxmlAttributes bag, CreationContext cc)
            {
                base.Init(ve, bag, cc);
                var g = (PianoRollGrid)ve;
                g.SetSize(_rows.GetValueFromBag(bag, cc), _cols.GetValueFromBag(bag, cc));
            }
        }

        // visuals
        private VisualElement[,] _cells;
        private Label[,] _labels;

        // data: ON/OFF per cell
        private bool[,] _on;

        // config
        private int _rows = 6;
        private int _cols = 8;

        // fallback row appearance (used when not using per-cell maps)
        private Color[] _rowColors;
        private Func<int, string> _rowLabeler;

        // ✅ exact per-cell label + color maps (used only when rows=6, cols=8)
        private string[,] _cellLabelMap; // [r,c]
        private Color[,] _cellColorMap;  // [r,c]

        // paint behavior
        private bool _toggleOffOnClick = true;
        private bool _isDragging = false;
        private bool _dragTargetValue = true;

        // event
        public event Action<int, int, bool> OnCellChanged; // (row, col, isOn)

        // Picker mode: PianoRollGrid acts as a NOTE PICKER / palette (no self-painting)
        public event Action<int, int, string, int> OnCellPicked; // (row, col, label, valueId)

        private bool _pickerMode = false;
        private int _pickedRow = -1;
        private int _pickedCol = -1;

        public int RowCount => _rows;
        public int ColCount => _cols;

        public PianoRollGrid()
        {
            AddToClassList("km-pianoroll");

            // fallback defaults if not in 6x8 exact mode
            _rowLabeler = DefaultRowLabel;
            _rowColors = DefaultRowColors();

            Build();
        }

        // -----------------------------
        // Public API
        // -----------------------------

        public void SetSize(int rows, int cols)
        {
            rows = Mathf.Clamp(rows, 1, 6);
            cols = Mathf.Clamp(cols, 1, 8);

            _rows = rows;
            _cols = cols;

            // ✅ if exact size, install exact maps (labels + colors)
            if (_rows == 6 && _cols == 8)
            {
                _cellLabelMap = BuildExactLabelMap6x8();
                _cellColorMap = BuildExactColorMap6x8();
            }
            else
            {
                _cellLabelMap = null;
                _cellColorMap = null;
            }

            Build();
        }

        /// <summary>
        /// Optional: override per-row colors (only used when not in 6x8 exact mode).
        /// </summary>
        public void SetRowColors(Color[] colors)
        {
            _rowColors = (colors != null && colors.Length > 0) ? colors : DefaultRowColors();
            RefreshAll();
        }

        /// <summary>
        /// Optional: override per-row labeler (only used when not in 6x8 exact mode).
        /// </summary>
        public void SetRowLabeler(Func<int, string> labeler)
        {
            _rowLabeler = labeler ?? DefaultRowLabel;
            RefreshAll();
        }

        public void EnableToggleOffOnClick(bool on)
        {
            _toggleOffOnClick = on;
        }

        

        /// <summary>
        /// When enabled, the grid no longer toggles its own on/off cells.
        /// Instead, clicking/dragging picks a note (row/col) and fires OnCellPicked.
        /// </summary>
        public void EnablePickerMode(bool on)
        {
            _pickerMode = on;

            // Reset highlight when switching modes.
            if (!_pickerMode)
            {
                _pickedRow = -1;
                _pickedCol = -1;
            }

            RefreshAll();
        }

        /// <summary>
        /// Stable 1-based value id for (row,col). Useful for writing into StepGrid (0 = empty).
        /// </summary>
        public int GetCellValueId(int r, int c) => (r * _cols) + c + 1;

        /// <summary>
        /// Public label accessor for a cell (uses exact map when available).
        /// </summary>
        public string GetCellLabel(int r, int c) => GetCellLabelInternal(r, c);

        /// <summary>
        /// Public base color accessor for a cell (uses exact map when available).
        /// </summary>
        public Color GetCellColor(int r, int c) => GetCellBaseColor(r, c);

public void ClearAll(bool fireEvent = false)
        {
            if (_on == null) return;
            for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                SetCell(r, c, false, fireEvent);
        }

        public bool GetCell(int r, int c) => _on != null && _on[r, c];

        public void SetCell(int r, int c, bool isOn, bool fireEvent = true)
        {
            if (_on == null) return;
            if (r < 0 || r >= _rows || c < 0 || c >= _cols) return;

            if (_on[r, c] == isOn)
            {
                UpdateCellVisual(r, c);
                return;
            }

            _on[r, c] = isOn;
            UpdateCellVisual(r, c);

            if (fireEvent)
                OnCellChanged?.Invoke(r, c, isOn);
        }

        public bool[,] ExportBools()
        {
            var dst = new bool[_rows, _cols];
            if (_on == null) return dst;
            Array.Copy(_on, dst, _on.Length);
            return dst;
        }

        public void ImportBools(bool[,] src, bool fireEvent = false)
        {
            if (src == null) return;
            if (src.GetLength(0) != _rows || src.GetLength(1) != _cols) return;

            for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                SetCell(r, c, src[r, c], fireEvent);
        }

        public void RefreshAll()
        {
            if (_cells == null) return;
            for (int r = 0; r < _rows; r++)
            for (int c = 0; c < _cols; c++)
                UpdateCellVisual(r, c);
        }

        // -----------------------------
        // Build + visuals
        // -----------------------------

        private void Build()
        {
            Clear();

            _on = new bool[_rows, _cols];
            _cells = new VisualElement[_rows, _cols];
            _labels = new Label[_rows, _cols];

            var outer = new VisualElement();
            outer.AddToClassList("km-pianoroll-outer");
            Add(outer);

            for (int r = 0; r < _rows; r++)
            {
                var row = new VisualElement();
                row.AddToClassList("km-pianoroll-row");
                outer.Add(row);

                for (int c = 0; c < _cols; c++)
                {
                    int rr = r, cc = c;

                    var cell = new VisualElement();
                    cell.AddToClassList("km-pianoroll-cell");
                    cell.style.position = Position.Relative;

                    var label = new Label();
                    label.AddToClassList("km-pianoroll-label");
                    label.pickingMode = PickingMode.Ignore;
                    label.style.position = Position.Absolute;
                    label.style.left = 0;
                    label.style.right = 0;
                    label.style.top = 0;
                    label.style.bottom = 0;
                    label.style.unityTextAlign = TextAnchor.MiddleCenter;

                    cell.Add(label);

                    _cells[r, c] = cell;
                    _labels[r, c] = label;

                    // pointer interactions
                    cell.RegisterCallback<PointerDownEvent>(e =>
                    {
                        _isDragging = true;

                        // Picker mode: pick note + highlight
                        if (_pickerMode)
                        {
                            _pickedRow = rr;
                            _pickedCol = cc;

                            var labelText = GetCellLabelInternal(rr, cc);
                            var valueId = GetCellValueId(rr, cc);

                            OnCellPicked?.Invoke(rr, cc, labelText, valueId);

                            RefreshAll();

                            cell.CapturePointer(e.pointerId);
                            e.StopPropagation();
                            return;
                        }

                        // Paint mode (legacy): toggle on/off
                        bool cur = GetCell(rr, cc);
                        bool next;

                        // toggle if already on
                        if (_toggleOffOnClick && cur)
                            next = false;
                        else
                            next = true;

                        _dragTargetValue = next;
                        ApplyDrag(rr, cc);

                        cell.CapturePointer(e.pointerId);
                        e.StopPropagation();
                    });
cell.RegisterCallback<PointerMoveEvent>(e =>
                    {
                        if (!_isDragging) return;
                        if (!cell.HasPointerCapture(e.pointerId)) return;

                        if (_pickerMode)
                        {
                            // Dragging across cells updates the picked note
                            if (_pickedRow != rr || _pickedCol != cc)
                            {
                                _pickedRow = rr;
                                _pickedCol = cc;

                                var labelText = GetCellLabelInternal(rr, cc);
                                var valueId = GetCellValueId(rr, cc);

                                OnCellPicked?.Invoke(rr, cc, labelText, valueId);
                                RefreshAll();
                            }
                            return;
                        }

                        ApplyDrag(rr, cc);
                    });
cell.RegisterCallback<PointerUpEvent>(e =>
                    {
                        if (cell.HasPointerCapture(e.pointerId))
                            cell.ReleasePointer(e.pointerId);

                        _isDragging = false;
                    });

                    row.Add(cell);
                }
            }

            RefreshAll();
        }

        private void ApplyDrag(int r, int c)
        {
            SetCell(r, c, _dragTargetValue);
        }

        private void UpdateCellVisual(int r, int c)
        {
            var cell = _cells[r, c];
            var label = _labels[r, c];
            if (cell == null || label == null) return;

            // ✅ Per-cell base color if we have the exact map, else fallback to per-row
            var baseColor = GetCellBaseColor(r, c);
            bool isOn = _on[r, c];

            // Picker highlight (independent of ON/OFF state)
            bool isPicked = _pickerMode && (r == _pickedRow) && (c == _pickedCol);
            // OFF is dimmer; ON is bright (like the reference image tiles)
            var off = new Color(baseColor.r * 0.85f, baseColor.g * 0.85f, baseColor.b * 0.85f, 0.35f);
            var on  = new Color(baseColor.r, baseColor.g, baseColor.b, 0.95f);

            cell.style.backgroundColor = isOn ? on : off;

            // white border pixel look (USS can override)
            cell.style.borderTopWidth = isPicked ? 4 : 2;
            cell.style.borderBottomWidth = isPicked ? 4 : 2;
            cell.style.borderLeftWidth = isPicked ? 4 : 2;
            cell.style.borderRightWidth = isPicked ? 4 : 2;
            cell.style.borderTopColor = Color.white;
            cell.style.borderBottomColor = Color.white;
            cell.style.borderLeftColor = Color.white;
            cell.style.borderRightColor = Color.white;

            // ✅ label exactly like image (per-cell) if we have the map
            label.text = GetCellLabelInternal(r, c);
        }

        private string GetCellLabelInternal(int r, int c)
        {
            if (_cellLabelMap != null &&
                _cellLabelMap.GetLength(0) == _rows &&
                _cellLabelMap.GetLength(1) == _cols)
            {
                return _cellLabelMap[r, c] ?? "";
            }

            // fallback: row-based labeler
            return _rowLabeler != null ? _rowLabeler(r) : "";
        }

        private Color GetCellBaseColor(int r, int c)
        {
            if (_cellColorMap != null &&
                _cellColorMap.GetLength(0) == _rows &&
                _cellColorMap.GetLength(1) == _cols)
            {
                return _cellColorMap[r, c];
            }

            // fallback: row-based color banding
            if (_rowColors == null || _rowColors.Length == 0)
                return new Color(0.2f, 0.2f, 0.2f, 1f);

            return _rowColors[r % _rowColors.Length];
        }

        // -----------------------------
        // ✅ Exact 6x8 layout (matches your image)
        // -----------------------------

        private static string[,] BuildExactLabelMap6x8()
        {
            // Top-left to bottom-right, exactly:
            //
            // Row 0: A A B C C D D E
            // Row 1: F F G G A A B C
            // Row 2: C D D E F F G G
            // Row 3: A A B C C D D E
            // Row 4: F F G G A A B C
            // Row 5: C D D E F F G G
            return new string[6, 8]
            {
                { "A","A","B","C","C","D","D","E" },
                { "F","F","G","G","A","A","B","C" },
                { "C","D","D","E","F","F","G","G" },
                { "A","A","B","C","C","D","D","E" },
                { "F","F","G","G","A","A","B","C" },
                { "C","D","D","E","F","F","G","G" },
            };
        }

        private static Color[,] BuildExactColorMap6x8()
        {
            // Palette approximations to the reference image.
            // Light/Dark variants give the “paired tile” contrast.
            Color OR_L = new Color(0.95f, 0.55f, 0.25f, 1f);
            Color OR_D = new Color(0.65f, 0.20f, 0.10f, 1f);

            Color GR_L = new Color(0.70f, 0.88f, 0.45f, 1f);
            Color GR_D = new Color(0.20f, 0.45f, 0.20f, 1f);

            Color BL_L = new Color(0.30f, 0.80f, 0.95f, 1f);
            Color BL_D = new Color(0.10f, 0.35f, 0.65f, 1f);

            Color PU_L = new Color(0.62f, 0.50f, 0.88f, 1f);
            Color PU_D = new Color(0.20f, 0.15f, 0.50f, 1f);

            // The reference shows hue blocks:
            // - Row0: orange
            // - Row1: orange then green
            // - Row2: green
            // - Row3: blue
            // - Row4: blue then purple
            // - Row5: purple
            //
            // We also mirror the “pair” contrast (light/dark) inside AA, CC, DD, etc.
            return new Color[6, 8]
            {
                // Row 0: A A B C C D D E  (orange)
                { OR_L, OR_D, OR_L, OR_L, OR_D, OR_L, OR_D, OR_L },

                // Row 1: F F G G A A B C  (orange, then green)
                { OR_L, OR_D, OR_L, OR_D, GR_L, GR_D, GR_L, GR_L },

                // Row 2: C D D E F F G G  (green)
                { GR_D, GR_L, GR_D, GR_L, GR_L, GR_D, GR_L, GR_D },

                // Row 3: A A B C C D D E  (blue)
                { BL_L, BL_D, BL_L, BL_L, BL_D, BL_L, BL_D, BL_L },

                // Row 4: F F G G A A B C  (blue, then purple)
                { BL_L, BL_D, BL_L, BL_D, PU_L, PU_D, PU_L, PU_L },

                // Row 5: C D D E F F G G  (purple)
                { PU_D, PU_L, PU_D, PU_L, PU_L, PU_D, PU_L, PU_D },
            };
        }

        // -----------------------------
        // Fallback label/colors (only used when not 6x8 exact)
        // -----------------------------

        private static string DefaultRowLabel(int r)
        {
            // fallback only
            string[] notes = { "A", "A", "B", "C", "C", "D", "D", "E", "F", "F", "G", "G" };
            return notes[r % notes.Length];
        }

        private static Color[] DefaultRowColors()
        {
            // fallback only
            return new[]
            {
                new Color(0.95f, 0.55f, 0.25f, 1f),
                new Color(0.85f, 0.25f, 0.15f, 1f),
                new Color(0.55f, 0.80f, 0.35f, 1f),
                new Color(0.20f, 0.55f, 0.20f, 1f),
                new Color(0.30f, 0.75f, 0.95f, 1f),
                new Color(0.10f, 0.35f, 0.65f, 1f),
            };
        }
    }
}