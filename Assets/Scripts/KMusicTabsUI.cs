using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic;       // <-- IMPORTANT: gives access to KMusicApp
using KMusic.Core;
using KMusic.UI;

public class KMusicTabsUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    private ParameterBus _bus;
    private Coroutine _co;

    private VisualElement tabSeq, tabSynth, tabSampler, tabArp, tabFx;
    private VisualElement pageSeq, pageSynth, pageSampler, pageArp, pageFx;

    private bool _isSetup = false;

    // Add these fields near the top of the class:
    private bool _boundSeq, _boundSynth, _boundSampler, _boundArp, _boundFx;

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        if (!doc) Debug.LogError("KMusicTabsUI: No UIDocument found on this GameObject.");
    }

    private void OnEnable()
    {
        Setup();

        if (_co != null) StopCoroutine(_co);
        _co = StartCoroutine(BindBusWhenReady());
    }

    private void OnDisable()
    {
        if (_co != null) StopCoroutine(_co);
        _co = null;

        _isSetup = false;
        _bus = null;

        _boundSeq = _boundSynth = _boundSampler = _boundArp = _boundFx = false;
    }
    
    private void Setup()
    {
        Debug.Log("KMusicTabsUI Setup() running");

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

    private IEnumerator BindBusWhenReady()
    {
        // Find KMusicApp INCLUDING inactive
        KMusicApp app = null;

#if UNITY_2023_1_OR_NEWER
        var apps = FindObjectsByType<KMusicApp>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var apps = Resources.FindObjectsOfTypeAll<KMusicApp>();
#endif
        if (apps != null && apps.Length > 0)
            app = apps[0];

        if (app == null)
        {
            Debug.LogWarning("KMusicTabsUI: No KMusicApp found in scene (even inactive).");
            yield break;
        }

        while (app.Bus == null)
            yield return null;

        _bus = app.Bus;
        Debug.Log("KMusicTabsUI: Bus bound OK.");

        // Bind whatever page is visible right now
        BindVisiblePages();
    }

    private void BindVisiblePages()
    {
        if (_bus == null) return;

        if (pageSeq != null && pageSeq.resolvedStyle.display != DisplayStyle.None) BindAll(pageSeq, _bus);
        if (pageSynth != null && pageSynth.resolvedStyle.display != DisplayStyle.None) BindAll(pageSynth, _bus);
        if (pageSampler != null && pageSampler.resolvedStyle.display != DisplayStyle.None) BindAll(pageSampler, _bus);
        if (pageArp != null && pageArp.resolvedStyle.display != DisplayStyle.None) BindAll(pageArp, _bus);
        if (pageFx != null && pageFx.resolvedStyle.display != DisplayStyle.None) BindAll(pageFx, _bus);
    }

    private void RegisterTab(VisualElement tab, System.Action onActivate)
    {
        if (tab == null) return;

        tab.style.flexGrow = 1;
        tab.style.minHeight = 40;
        tab.pickingMode = PickingMode.Position;

        tab.RegisterCallback<PointerDownEvent>(evt =>
        {
            Debug.Log($"TAB DOWN: {tab.name}");
            onActivate?.Invoke();
            evt.StopPropagation();
        }, TrickleDown.TrickleDown);
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

        // Bind only once per page
        if (_bus == null) return;

        if (which == "seq" && !_boundSeq) { BindAll(pageSeq, _bus); _boundSeq = true; }
        else if (which == "synth" && !_boundSynth) { BindAll(pageSynth, _bus); _boundSynth = true; }
        else if (which == "sampler" && !_boundSampler) { BindAll(pageSampler, _bus); _boundSampler = true; }
        else if (which == "arp" && !_boundArp) { BindAll(pageArp, _bus); _boundArp = true; }
        else if (which == "fx" && !_boundFx) { BindAll(pageFx, _bus); _boundFx = true; }
    }

    private static void BindAll(VisualElement root, ParameterBus bus)
    {
        if (root == null || bus == null) return;

        var stack = new Stack<VisualElement>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var ve = stack.Pop();

            if (ve is IParamBindable bindable)
                bindable.Bind(bus);

            foreach (var child in ve.Children())
                stack.Push(child);
        }
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