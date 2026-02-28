using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;

public class KMusicSamplerPaintUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    private StepGrid sampleGrid;
    private StepGrid drumGrid;

    private Button[] chopBtns = new Button[16];
    private Button[] drumBtns = new Button[8];

    private int activeChop = 1; // 1..16
    private int activeDrum = 0; // 0..7

    // Saved patterns:
    // Sample: each cell holds 0..16 (chop id)
    private int[,] samplePattern = new int[2, 8];

    // Drums: 8 lanes, each is bool[2,8]
    private bool[][,] drumPatterns = new bool[8][,];

    // Fallback lane colors (used if drum buttons don't have a backgroundColor in USS)
    private static readonly Color[] FALLBACK_DRUM_COLORS = new Color[8]
    {
        new Color(0.90f, 0.55f, 0.20f, 1f), // Kick
        new Color(0.95f, 0.30f, 0.35f, 1f), // Snare
        new Color(0.85f, 0.35f, 0.90f, 1f), // Clap
        new Color(0.35f, 0.85f, 0.95f, 1f), // Hat C
        new Color(0.25f, 0.95f, 0.55f, 1f), // Hat O
        new Color(0.95f, 0.90f, 0.25f, 1f), // Rim
        new Color(0.55f, 0.70f, 0.95f, 1f), // Perc
        new Color(0.95f, 0.60f, 0.80f, 1f), // Crash
    };

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
    }

    private void OnEnable()
    {
        if (!doc) return;

        var root = doc.rootVisualElement;

        sampleGrid = root.Q<StepGrid>("SampleStepGrid") ?? root.Q<StepGrid>("SampleStepGrid");
        drumGrid   = root.Q<StepGrid>("DrumStepGrid");

        if (sampleGrid == null) Debug.LogError("KMusicSamplerPaintUI: SampleStepGrid not found in UXML.");
        if (drumGrid == null)   Debug.LogError("KMusicSamplerPaintUI: DrumStepGrid not found in UXML.");

        // Chop buttons (Chop01..Chop16)
        for (int i = 0; i < 16; i++)
        {
            string name = $"Chop{(i + 1):00}";
            chopBtns[i] = root.Q<Button>(name);
        }

        // Drum buttons
        drumBtns[0] = root.Q<Button>("DrumKick");
        drumBtns[1] = root.Q<Button>("DrumSnare");
        drumBtns[2] = root.Q<Button>("DrumClap");
        drumBtns[3] = root.Q<Button>("DrumHatC");
        drumBtns[4] = root.Q<Button>("DrumHatO");
        drumBtns[5] = root.Q<Button>("DrumRim");
        drumBtns[6] = root.Q<Button>("DrumPerc");
        drumBtns[7] = root.Q<Button>("DrumCrash");

        // Init lane memories
        for (int d = 0; d < 8; d++)
            drumPatterns[d] = new bool[2, 8]; // all false by default = “cleared grid”

        WireChops();
        WireDrums();
        WireGrids();

        // Sample grid: show chop number on active cells
        sampleGrid?.EnableValueLabels(true, (v) => (v >= 1 && v <= 16) ? v.ToString("00") : "");

        // Default: no highlights by default
        sampleGrid?.ClearAll();
        drumGrid?.ClearAll();

        // ✅ Sample grid: clicking an already-on cell erases it (toggle)
        sampleGrid?.EnableToggleEraseOnSameValue(true);

        // ✅ Drum grid: clicking an already-on cell erases it (toggle)
        drumGrid?.EnableToggleEraseOnSameValue(true);

        // Set default brushes
        SelectChop(1);
        SelectDrum(0);
        LoadDrumLaneToGrid(0);
    }

    // --- Chops (Sample Sequencer) ---
    private void WireChops()
    {
        for (int i = 0; i < 16; i++)
        {
            int chopId = i + 1;
            var btn = chopBtns[i];
            if (btn == null) continue;

            btn.clicked += () => SelectChop(chopId);
        }
    }

    private void SelectChop(int chopId)
    {
        activeChop = Mathf.Clamp(chopId, 1, 16);

        // paint brush = chop id
        if (sampleGrid != null)
            sampleGrid.SetPaintValue(activeChop);

        // optional active UI class
        for (int i = 0; i < 16; i++)
        {
            var b = chopBtns[i];
            if (b == null) continue;
            if (i + 1 == activeChop) b.AddToClassList("km-chop-btn--active");
            else b.RemoveFromClassList("km-chop-btn--active");
        }
    }

    // --- Drums (Lane-based) ---
    private void WireDrums()
    {
        for (int i = 0; i < 8; i++)
        {
            int lane = i;
            var btn = drumBtns[i];
            if (btn == null) continue;

            btn.clicked += () => SelectDrum(lane);
        }
    }

    private void SelectDrum(int lane)
    {
        lane = Mathf.Clamp(lane, 0, 7);

        // Save current visible lane back into memory before switching
        SaveGridToDrumLane(activeDrum);

        activeDrum = lane;

        // Show new lane (grid appears “cleared” if that lane empty)
        LoadDrumLaneToGrid(activeDrum);

        // Drums: tint painted steps by the selected drum's color
        if (drumGrid != null)
        {
            drumGrid.SetActiveTint(GetActiveDrumColor(activeDrum));
            drumGrid.RefreshAll(); // ✅ immediate recolor of already-painted steps
        }

        // brush for drums is always 1 (on) or 0 erase via modifier
        if (drumGrid != null)
            drumGrid.SetPaintValue(1);

        for (int i = 0; i < 8; i++)
        {
            var b = drumBtns[i];
            if (b == null) continue;
            if (i == activeDrum) b.AddToClassList("km-drum-btn--active");
            else b.RemoveFromClassList("km-drum-btn--active");
        }
    }

    private void SaveGridToDrumLane(int lane)
    {
        if (drumGrid == null) return;
        var b = drumGrid.ExportBools();
        drumPatterns[lane] = b;
    }

    private void LoadDrumLaneToGrid(int lane)
    {
        if (drumGrid == null) return;
        drumGrid.ClearAll();
        drumGrid.ImportBools(drumPatterns[lane], fireEvent: false);
    }

    private Color GetActiveDrumColor(int lane)
    {
        lane = Mathf.Clamp(lane, 0, 7);

        // Your drum lane colors are defined via USS border-color (e.g. .drum-kick { border-color: ... })
        var btn = drumBtns[lane];
        if (btn != null)
        {
            var c = btn.resolvedStyle.borderTopColor; // border color is where the drum color lives
            if (c.a > 0.01f)
                return c;
        }

        return FALLBACK_DRUM_COLORS[lane];
    }

    // --- Grid wiring (“paint” + “other way”) ---
    private void WireGrids()
    {
        if (sampleGrid != null)
        {
            // Keep samplePattern updated when user paints
            sampleGrid.OnCellValueChanged += (r, c, v) =>
            {
                samplePattern[r, c] = v;
            };

            // “Other way”: clicking a painted cell selects that chop
            sampleGrid.OnCellClicked += (r, c) =>
            {
                int v = sampleGrid.GetValue(r, c);
                if (v >= 1 && v <= 16)
                    SelectChop(v);
            };
        }

        if (drumGrid != null)
        {
            // Drums: when user paints, update only the active lane memory
            drumGrid.OnCellValueChanged += (r, c, v) =>
            {
                drumPatterns[activeDrum][r, c] = (v != 0);
            };

            // “Other way” for drums:
            // clicking a cell doesn't change lane (because lane is chosen by drum picker)
            // but we can still support “erase on click if already on” naturally via toggle logic.
        }
    }
}