using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KMusic;       // KMusicApp
using KMusic.Core;
using KMusic.UI;

public class KMusicTabsUI : MonoBehaviour
{
    [SerializeField] private UIDocument doc;

    private ParameterBus _bus;
    private Coroutine _co;

    // Tabs in UXML:
    // TabSampler, TabSeq, TabPad, TabPlayer, TabSynth, TabSynth2, TabFx
    private VisualElement tabSeq, tabSynth, tabSynth2, tabSampler, tabPad, tabPlayer, tabFx;

    // Pages in UXML:
    // PageSeq, PageSynth, PageSynth2, PagePad, PageSampler, PagePlayer, PageFx
    private VisualElement pageSeq, pageSynth, pageSynth2, pagePad, pageSampler, pagePlayer, pageFx;

    private bool _isSetup = false;

    // Bind once per page
    private bool _boundSeq, _boundSynth, _boundPad, _boundSampler, _boundPlayer, _boundFx;

    // Which tab opens on launch
    [SerializeField] private string defaultTab = "sampler"; // "sampler","seq","pad","synth","synth2","player","fx"

    private void Awake()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        if (!doc) Debug.LogError("KMusicTabsUI: No UIDocument found on this GameObject.");
    }

    private void OnEnable()
    {
        if (GetComponent<KMusicPadSynthRuntime>() == null)
            gameObject.AddComponent<KMusicPadSynthRuntime>();

        if (_co != null) StopCoroutine(_co);
        _co = StartCoroutine(SetupThenBind());
    }

    private void OnDisable()
    {
        if (_co != null) StopCoroutine(_co);
        _co = null;

        _isSetup = false;
        _bus = null;

        _boundSeq = _boundSynth = _boundPad = _boundSampler = _boundPlayer = _boundFx = false;
    }

    private IEnumerator SetupThenBind()
    {
        if (!doc) doc = GetComponent<UIDocument>();
        if (!doc)
        {
            Debug.LogError("KMusicTabsUI: No UIDocument found on this GameObject.");
            yield break;
        }

        // Wait until UI Toolkit has built the visual tree (common on first enable)
        while (doc.rootVisualElement == null)
            yield return null;

        Setup();

        // Bind bus after setup so pages/tabs exist
        yield return BindBusWhenReady();
    }

    private void Setup()
    {
        if (_isSetup) return;
        if (!doc) return;

        var root = doc.rootVisualElement;
        if (root == null) return;

        // Tabs
        tabSeq     = root.Q<VisualElement>("TabSeq");
        tabSynth   = root.Q<VisualElement>("TabSynth");
        tabSynth2  = root.Q<VisualElement>("TabSynth2");
        tabSampler = root.Q<VisualElement>("TabSampler");
        tabPad     = root.Q<VisualElement>("TabPad");
        tabPlayer  = root.Q<VisualElement>("TabPlayer");
        tabFx      = root.Q<VisualElement>("TabFx");

        // Pages
        pageSeq     = root.Q<VisualElement>("PageSeq");
        pageSynth   = root.Q<VisualElement>("PageSynth");
        pageSynth2  = root.Q<VisualElement>("PageSynth2");
        pagePad     = root.Q<VisualElement>("PagePad");
        pageSampler = root.Q<VisualElement>("PageSampler");
        pagePlayer  = root.Q<VisualElement>("PagePlayer");
        pageFx      = root.Q<VisualElement>("PageFx");

        LogMissing("TabSeq", tabSeq);
        LogMissing("TabSynth", tabSynth);
        LogMissing("TabSynth2", tabSynth2);
        LogMissing("TabSampler", tabSampler);
        LogMissing("TabPad", tabPad);
        LogMissing("TabPlayer", tabPlayer);
        LogMissing("TabFx", tabFx);

        LogMissing("PageSeq", pageSeq);
        LogMissing("PageSynth", pageSynth);
        LogMissing("PageSynth2", pageSynth2);
        LogMissing("PagePad", pagePad);
        LogMissing("PageSampler", pageSampler);
        LogMissing("PagePlayer", pagePlayer);
        LogMissing("PageFx", pageFx);

        // If Player page isn't in UXML yet, hide the tab so you don't get dead clicks.
        if (tabPlayer != null && pagePlayer == null)
            tabPlayer.style.display = DisplayStyle.None;

        RegisterTab(tabSeq,     () => Show("seq"));
        RegisterTab(tabSynth,   () => Show("synth"));
        RegisterTab(tabSynth2,  () => Show("synth2"));
        RegisterTab(tabPad,     () => Show("pad"));
        RegisterTab(tabSampler, () => Show("sampler"));

        // Only register Player if the page exists
        if (tabPlayer != null && pagePlayer != null)
            RegisterTab(tabPlayer, () => Show("player"));

        RegisterTab(tabFx,      () => Show("fx"));

        // default
        var def = (defaultTab ?? "sampler").ToLowerInvariant();
        if (def == "player" && pagePlayer == null) def = "sampler";
        Show(def);

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

        // Bind whatever page is visible right now
        BindVisiblePages();
    }

    private void BindVisiblePages()
    {
        if (_bus == null) return;

        if (pageSeq != null && pageSeq.resolvedStyle.display != DisplayStyle.None) BindAll(pageSeq, _bus);
        if (pageSynth != null && pageSynth.resolvedStyle.display != DisplayStyle.None) BindAll(pageSynth, _bus);
        // PageSynth2 is driven by KMusicPadSynthRuntime with its own local bus.
        if (pagePad != null && pagePad.resolvedStyle.display != DisplayStyle.None) BindAll(pagePad, _bus);
        if (pageSampler != null && pageSampler.resolvedStyle.display != DisplayStyle.None) BindAll(pageSampler, _bus);
        if (pagePlayer != null && pagePlayer.resolvedStyle.display != DisplayStyle.None) BindAll(pagePlayer, _bus);
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
            onActivate?.Invoke();
            evt.StopPropagation();
        }, TrickleDown.TrickleDown);
    }

    private void Show(string which)
    {
        which = (which ?? "sampler").ToLowerInvariant();
        if (which == "player" && pagePlayer == null) which = "sampler";

        SetVisible(pageSeq, which == "seq");
        SetVisible(pageSynth, which == "synth");
        SetVisible(pageSynth2, which == "synth2");
        SetVisible(pagePad, which == "pad");
        SetVisible(pageSampler, which == "sampler");
        SetVisible(pagePlayer, which == "player");
        SetVisible(pageFx, which == "fx");

        SetActive(tabSeq, which == "seq");
        SetActive(tabSynth, which == "synth");
        SetActive(tabSynth2, which == "synth2");
        SetActive(tabPad, which == "pad");
        SetActive(tabSampler, which == "sampler");
        SetActive(tabPlayer, which == "player");
        SetActive(tabFx, which == "fx");

        // Bind only once per page (after bus exists)
        if (_bus == null) return;

        if (which == "seq" && !_boundSeq) { BindAll(pageSeq, _bus); _boundSeq = true; }
        else if (which == "synth" && !_boundSynth) { BindAll(pageSynth, _bus); _boundSynth = true; }
        else if (which == "pad" && !_boundPad) { BindAll(pagePad, _bus); _boundPad = true; }
        else if (which == "sampler" && !_boundSampler) { BindAll(pageSampler, _bus); _boundSampler = true; }
        else if (which == "player" && !_boundPlayer) { BindAll(pagePlayer, _bus); _boundPlayer = true; }
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
