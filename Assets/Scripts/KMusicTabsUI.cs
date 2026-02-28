using UnityEngine;
using UnityEngine.UIElements;

public class KMusicTabsUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    private VisualElement tabSeq, tabSynth, tabSampler, tabArp, tabFx;
    private VisualElement pageSeq, pageSynth, pageSampler, pageArp, pageFx;

    private VisualElement _pressedTab;
    private int _pressedPointerId = -1;

    private bool _isSetup = false;

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        if (!doc) Debug.LogError("KMusicTabsUI: No UIDocument found on this GameObject.");
    }

    private void OnEnable()
    {
        // UIDocument builds/clones the visual tree here — safe time to query.
        Setup();
    }

    private void OnDisable()
    {
        _isSetup = false;
        _pressedTab = null;
        _pressedPointerId = -1;
        // (Optional: if you store delegates, you can UnregisterCallback here)
    }

    private void Setup()
    {
        if (_isSetup) return;
        if (!doc) return;

        var root = doc.rootVisualElement;
        if (root == null)
        {
            Debug.LogWarning("KMusicTabsUI: rootVisualElement is null (UIDocument not ready).");
            return;
        }

        // Tabs
        tabSeq     = root.Q<VisualElement>("TabSeq");
        tabSynth   = root.Q<VisualElement>("TabSynth");
        tabSampler = root.Q<VisualElement>("TabSampler");
        tabArp     = root.Q<VisualElement>("TabArp");
        tabFx      = root.Q<VisualElement>("TabFx");

        // Pages
        pageSeq     = root.Q<VisualElement>("PageSeq");
        pageSynth   = root.Q<VisualElement>("PageSynth");
        pageSampler = root.Q<VisualElement>("PageSampler");
        pageArp     = root.Q<VisualElement>("PageArp");
        pageFx      = root.Q<VisualElement>("PageFx");

        LogMissing("TabSeq", tabSeq);
        LogMissing("TabSynth", tabSynth);
        LogMissing("TabSampler", tabSampler);
        LogMissing("TabArp", tabArp);
        LogMissing("TabFx", tabFx);

        LogMissing("PageSeq", pageSeq);
        LogMissing("PageSynth", pageSynth);
        LogMissing("PageSampler", pageSampler);
        LogMissing("PageArp", pageArp);
        LogMissing("PageFx", pageFx);

        RegisterTab(tabSeq,     () => Show("seq"));
        RegisterTab(tabSynth,   () => Show("synth"));
        RegisterTab(tabSampler, () => Show("sampler"));
        RegisterTab(tabArp,     () => Show("arp"));
        RegisterTab(tabFx,      () => Show("fx"));

        Show("seq");
        _isSetup = true;
    }

    private void RegisterTab(VisualElement tab, System.Action onActivate)
    {
        if (tab == null) return;

        tab.pickingMode = PickingMode.Position;
        foreach (var child in tab.Children())
            child.pickingMode = PickingMode.Ignore;

        tab.AddManipulator(new Clickable(() =>
        {
            Debug.Log($"TAB CLICK: {tab.name}");
            onActivate();
        }));
    }

    private void Show(string which)
    {
        SetVisible(pageSeq, which == "seq");
        SetVisible(pageSynth, which == "synth");
        SetVisible(pageSampler, which == "sampler");
        SetVisible(pageArp, which == "arp");
        SetVisible(pageFx, which == "fx");

        SetActive(tabSeq, which == "seq");
        SetActive(tabSynth, which == "synth");
        SetActive(tabSampler, which == "sampler");
        SetActive(tabArp, which == "arp");
        SetActive(tabFx, which == "fx");
    }

    private static void SetVisible(VisualElement ve, bool on)
    {
        if (ve == null) return;
        ve.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private static void SetActive(VisualElement tab, bool on)
    {
        if (tab == null) return;
        const string ACTIVE = "km-tab--active";
        if (on) tab.AddToClassList(ACTIVE);
        else tab.RemoveFromClassList(ACTIVE);
    }

    private static void LogMissing(string name, VisualElement ve)
    {
        if (ve != null) return;
        Debug.LogWarning($"KMusicTabsUI: Could not find '{name}' in UXML. Check the element 'name' attribute.");
    }
}