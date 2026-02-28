using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic.UI;

public class KMusicSampleSequencerUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    private StepGrid sampleGrid;
    private readonly Button[] chopBtns = new Button[16];

    private int activeChop = 1;                 // 1..16
    private readonly int[,] samplePattern = new int[2, 8]; // each cell = chop id (0 empty)

    private void Awake()
    {
        Debug.Log("[KMusicSampleSequencerUI] Awake fired");
        if (!doc) doc = GetComponent<UIDocument>();
    }

    private void OnEnable()
    {
        Debug.Log("[KMusicSampleSequencerUI] OnEnable fired");

        if (!doc)
        {
            Debug.LogError("[KMusicSampleSequencerUI] UIDocument not assigned and not found on this GameObject.");
            return;
        }

        var root = doc.rootVisualElement;
        if (root == null)
        {
            Debug.LogError("[KMusicSampleSequencerUI] rootVisualElement is null.");
            return;
        }

        // UI Toolkit: wait a tick so the visual tree is fully built (esp. if tabs swap content)
        root.schedule.Execute(() =>
        {
            // ---- FIND GRID (real fallbacks; your project commonly uses SamplerStepGrid) ----
            sampleGrid =
                root.Q<StepGrid>("SamplerStepGrid") ??
                root.Q<StepGrid>("SampleStepGrid") ??
                root.Q<StepGrid>("SampleSequencerGrid") ??
                root.Q<StepGrid>("SampleGrid");

            Debug.Log($"[KMusicSampleSequencerUI] sampleGrid found={(sampleGrid != null)}");

            if (sampleGrid == null)
            {
                Debug.LogError("[KMusicSampleSequencerUI] sample StepGrid not found. Check your UXML <km:StepGrid name=\"...\">");
                return;
            }

            // ---- FIND CHOP BUTTONS (Chop01..Chop16) ----
            for (int i = 0; i < 16; i++)
            {
                string name = $"Chop{(i + 1):00}";
                chopBtns[i] = root.Q<Button>(name);
                if (chopBtns[i] == null)
                    Debug.LogWarning($"[KMusicSampleSequencerUI] Button '{name}' not found in UXML.");
            }

            WireChops();
            WireGrid();

            // ---- LABELS ----
            sampleGrid.EnableValueLabels(true, v => (v >= 1 && v <= 16) ? v.ToString("00") : "");

            // Optional per-value tint if your StepGrid has it
            TryEnableValueTint(sampleGrid, true, GetChopColor);

            // Force a refresh so labels/tints show immediately
            ForceGridRefresh(sampleGrid);

            // Start clean + set default brush
            sampleGrid.ClearAll();
            ForceGridRefresh(sampleGrid);

            SelectChop(1);

        }).ExecuteLater(0);
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

            // Debug: you should see values 1..16 when painting
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