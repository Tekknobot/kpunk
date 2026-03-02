using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;

public class KMusicSampleSequencerUI : MonoBehaviour
{
    private const string PrefKey_SampleStepGrid = "kmusic.sample.stepgrid";

    [SerializeField] private UIDocument doc;

    private StepGrid sampleGrid;
    private StepGrid _lastBoundGrid;

    private KMusicDrumSequencer _sequencer;
    private readonly Button[] chopBtns = new Button[16];

    private int activeChop = 1;                 // 1..16
    private readonly int[,] samplePattern = new int[2, 8]; // each cell = chop id (0 empty)

    private IVisualElementScheduledItem _rebindLoop;

    private void Awake()
    {
        Debug.Log("[KMusicSampleSequencerUI] Awake fired");
        if (!doc) doc = GetComponent<UIDocument>();
    }

    private void OnEnable()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        if (!doc) return;

        var root = doc.rootVisualElement;
        if (root == null) return;

        // Poll because tabs often rebuild visual tree; we want to catch the grid when it exists.
        _rebindLoop?.Pause();
        _rebindLoop = root.schedule.Execute(() =>
        {
            var g =
                root.Q<StepGrid>("SamplerStepGrid") ??
                root.Q<StepGrid>("SampleStepGrid") ??
                root.Q<StepGrid>("SampleSequencerGrid") ??
                root.Q<StepGrid>("SampleGrid");

            if (g == null) return;

            // If UI rebuilt, we'll get a new StepGrid instance. Rebind + reload.
            if (g == _lastBoundGrid) return;
            _lastBoundGrid = g;

            sampleGrid = g;

            CacheChopButtons(root);
            if (_sequencer == null) _sequencer = FindObjectOfType<KMusicDrumSequencer>();

            // do your normal bind/wire
            WireChops();
            WireGrid();

            sampleGrid.EnableValueLabels(true, v => (v >= 1 && v <= 16) ? v.ToString("00") : "");
            TryEnableValueTint(sampleGrid, true, GetChopColor);

            // LOAD AFTER everything is wired
            var saved = KMusic.KMusicSaveState.LoadIntArray(PrefKey_SampleStepGrid, sampleGrid.RowCount * sampleGrid.ColCount);
            if (saved != null)
                sampleGrid.ImportValuesFlat(saved, fireEvent: false);
            else
                sampleGrid.ClearAll();

            ForceGridRefresh(sampleGrid);
            SelectChop(activeChop);
        }).Every(100); // 10x per second is fine for UI binding
    }

    private void OnDisable()
    {
        SavePattern();
        _rebindLoop?.Pause();
    }

    private void OnApplicationPause(bool pause)
    {
        if (pause) SavePattern();
    }

    private void OnApplicationQuit()
    {
        SavePattern();
    }

    private void SavePattern()
    {
        if (sampleGrid == null) return;
        KMusic.KMusicSaveState.SaveIntArray(PrefKey_SampleStepGrid, sampleGrid.ExportValuesFlat());
    }

    private void CacheChopButtons(VisualElement root)
    {
        if (root == null) return;
        for (int i = 0; i < 16; i++)
        {
            var b = root.Q<Button>($"Chop{(i + 1):00}");
            if (b != null) chopBtns[i] = b;
        }
    }

    private void WireChops()
    {
        for (int i = 0; i < 16; i++)
        {
            int chopId = i + 1;
            var btn = chopBtns[i];
            if (btn == null) continue;

            // Avoid duplicate subscriptions if OnEnable runs again:
            btn.clicked -= () => { }; // no-op; can't remove anonymous reliably

            // Instead: use a captured local and guard with a classlist marker.
            // If the button already has our marker, don’t rewire.
            const string wiredClass = "km-chop-btn--wired";
            if (btn.ClassListContains(wiredClass))
                continue;

            btn.AddToClassList(wiredClass);
            btn.clicked += () => SelectChop(chopId);
        }
    }

    private void SelectChop(int chopId)
    {
        activeChop = Mathf.Clamp(chopId, 1, 16);

        if (_sequencer == null) _sequencer = FindObjectOfType<KMusicDrumSequencer>();
        _sequencer?.AuditionChop(activeChop);

        if (sampleGrid != null)
        {
            sampleGrid.SetPaintValue(activeChop);
            ForceGridRefresh(sampleGrid);
        }

        for (int i = 0; i < 16; i++)
        {
            var b = chopBtns[i];
            if (b == null) continue;

            if (i + 1 == activeChop) b.AddToClassList("km-chop-btn--active");
            else b.RemoveFromClassList("km-chop-btn--active");
        }
    }

    private void WireGrid()
    {
        // Guard against double-wiring if OnEnable runs again
        // (We can’t easily unsubscribe lambdas, so we add a one-time marker on the grid.)
        const string gridWiredClass = "km-sample-grid--wired";
        if (sampleGrid.ClassListContains(gridWiredClass))
            return;

        sampleGrid.AddToClassList(gridWiredClass);

        sampleGrid.OnCellValueChanged += (r, c, v) =>
        {
            samplePattern[r, c] = v;

            int step = (r * 8) + c;
            if (_sequencer == null) _sequencer = FindObjectOfType<KMusicDrumSequencer>();
            if (_sequencer != null)
                _sequencer.SetSampleStep(step, v);

            // Persist immediately so playback survives tab rebuilds.
            SavePattern();

            Debug.Log($"[KMusicSampleSequencerUI] cell[{r},{c}]={v}");
            ForceGridRefresh(sampleGrid);
        };

        sampleGrid.OnCellClicked += (r, c) =>
        {
            int v = sampleGrid.GetValue(r, c);
            if (v >= 1 && v <= 16)
                SelectChop(v);
        };
    }

    public int[,] GetPatternCopy()
    {
        return sampleGrid != null ? sampleGrid.ExportValues() : (int[,])samplePattern.Clone();
    }

    public void LoadPattern(int[,] pat)
    {
        if (sampleGrid == null || pat == null) return;
        sampleGrid.ImportValues(pat, fireEvent: false);
        ForceGridRefresh(sampleGrid);
    }

    // ---------- Helpers ----------

    private static void TryEnableValueTint(StepGrid grid, bool on, Func<int, Color> colorFn)
    {
        if (grid == null) return;

        try
        {
            var mi = grid.GetType().GetMethod(
                "EnableValueTint",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (mi == null) return;

            mi.Invoke(grid, new object[] { on, colorFn });
        }
        catch
        {
            // optional
        }
    }

    private static void ForceGridRefresh(StepGrid grid)
    {
        if (grid == null) return;

        TryInvoke(grid, "RefreshAll");
        TryInvoke(grid, "Rebuild");
        TryInvoke(grid, "Refresh");
        TryInvoke(grid, "MarkDirtyRepaint");
    }

    private static void TryInvoke(object obj, string methodName)
    {
        try
        {
            var mi = obj.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi != null && mi.GetParameters().Length == 0)
                mi.Invoke(obj, null);
        }
        catch { }
    }

    private static Color GetChopColor(int chopId)
    {
        chopId = Mathf.Clamp(chopId, 1, 16);
        float h = (chopId - 1) / 16f;
        float s = 0.55f;
        float v = 0.90f;
        var c = Color.HSVToRGB(h, s, v);
        c.a = 1f;
        return c;
    }
}