using UnityEngine;
using UnityEngine.UIElements;
using KMusic.Core;

namespace KMusic
{
    public sealed class KMusicApp : MonoBehaviour
    {
        [Header("UI Toolkit")]
        public VisualTreeAsset mainUxml;
        public StyleSheet darkTheme;

        private UIDocument _doc;
        private ParameterBus _bus;
        private ThemeStyleSheet _runtimeTheme;

        private void Awake()
        {
            Application.targetFrameRate = 60;

            _doc = GetComponent<UIDocument>();
            if (_doc == null) _doc = gameObject.AddComponent<UIDocument>();

            if (mainUxml == null)
                mainUxml = Resources.Load<VisualTreeAsset>("KMusic/UI/Main");

            if (darkTheme == null)
                darkTheme = Resources.Load<StyleSheet>("KMusic/UI/kmusic_dark");

            if (_doc.panelSettings == null)
            {
                // Create panel settings at runtime (no editor asset needed).
                var ps = ScriptableObject.CreateInstance<PanelSettings>();
                ps.scaleMode = PanelScaleMode.ConstantPixelSize;
                ps.referenceResolution = new Vector2Int(1024, 2048);

                // IMPORTANT: UI Toolkit expects a ThemeStyleSheet on PanelSettings.
                // We create a minimal runtime ThemeStyleSheet to satisfy this requirement.
                if (_runtimeTheme == null)
                    _runtimeTheme = ScriptableObject.CreateInstance<ThemeStyleSheet>();
                ps.themeStyleSheet = _runtimeTheme;
_doc.panelSettings = ps;
            }

            if (mainUxml != null)
                _doc.visualTreeAsset = mainUxml;

            BuildParameters();
        }

        private void OnEnable()
        {
            var root = _doc.rootVisualElement;
            if (root == null) return;

            // --- Ensure background behind UI matches theme (prevents white notch/letterbox areas) ---
            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f); // ~#0F1115
            }

            // Fullscreen UI background element (covers even if safe-area padding moves content)
            var bg = root.Q<VisualElement>("KmBg");
            if (bg == null)
            {
                bg = new VisualElement { name = "KmBg" };
                bg.style.position = Position.Absolute;
                bg.style.left = 0;
                bg.style.top = 0;
                bg.style.right = 0;
                bg.style.bottom = 0;
                bg.style.backgroundColor = new Color(0.06f, 0.07f, 0.09f, 1f); // ~#0F1115
                root.Insert(0, bg);
            }

            // --- Safe-area padding (camera / notch) ---
            // Convert Screen.safeArea top inset (pixels) into UI reference pixels.
            var sa = Screen.safeArea;
            float topInsetPx = Screen.height - (sa.y + sa.height);
            float topInsetUI = 0f;

            if (_doc.panelSettings != null)
            {
                // Convert screen pixels -> reference pixels (works with ScaleWithScreenSize)
                var refRes = _doc.panelSettings.referenceResolution;
                topInsetUI = topInsetPx * (refRes.y / Mathf.Max(1f, Screen.height));
            }

            // Base padding + safe area inset
            float baseTop = 64f;
            root.style.paddingTop = baseTop + topInsetUI;

            if (darkTheme != null && !root.styleSheets.Contains(darkTheme))
                root.styleSheets.Add(darkTheme);

            // Bind all custom controls
            foreach (var b in root.Query<VisualElement>().ToList())
            {
                if (b is KMusic.UI.IParamBindable bindable)
                    bindable.Bind(_bus);
            }

            // Update BPM label from tempo param
            var bpmLabel = root.Q<Label>("BpmLabel");
            if (bpmLabel != null && _bus.TryGet("tempo", out var tempo))
                bpmLabel.text = tempo.Format();

            _bus.OnChanged += (id, v) =>
            {
                if (id == "tempo" && bpmLabel != null && _bus.TryGet("tempo", out var t))
                    bpmLabel.text = t.Format();
            };

            // Toggle preset browser on preset bar click
            var presetLabel = root.Q<Label>("PresetLabel");
            var presetBrowser = root.Q<VisualElement>("PresetBrowser");
            if (presetLabel != null && presetBrowser != null)
            {
                presetLabel.RegisterCallback<PointerDownEvent>(_ =>
                {
                    presetBrowser.style.display = presetBrowser.resolvedStyle.display == DisplayStyle.None
                        ? DisplayStyle.Flex
                        : DisplayStyle.None;
                });
            }
        }

        private void BuildParameters()
        {
            _bus = new ParameterBus();

            // Filter
            _bus.Add(new Parameter("filter.cutoff", 20, 20000, 3200, log:true, unit:"hz"));
            _bus.Add(new Parameter("filter.res", 0, 100, 30, unit:"%"));
            _bus.Add(new Parameter("filter.drive", 0, 100, 20, unit:"%"));
            _bus.Add(new Parameter("filter.atk", 0, 500, 120, unit:"ms"));
            _bus.Add(new Parameter("filter.dec", 0, 500, 220, unit:"ms"));
            _bus.Add(new Parameter("filter.sus", 0, 100, 60, unit:"%"));
            _bus.Add(new Parameter("filter.rel", 0, 500, 240, unit:"ms"));

            // Amp
            _bus.Add(new Parameter("amp.atk", 0, 500, 120, unit:"ms"));
            _bus.Add(new Parameter("amp.dec", 0, 500, 220, unit:"ms"));
            _bus.Add(new Parameter("amp.sus", 0, 100, 60, unit:"%"));
            _bus.Add(new Parameter("amp.atk2", 0, 500, 320, unit:"ms"));
            _bus.Add(new Parameter("amp.dec2", 0, 500, 240, unit:"ms"));
            _bus.Add(new Parameter("amp.sus2", 0, 100, 55, unit:"%"));
            _bus.Add(new Parameter("amp.rel2", 0, 500, 180, unit:"ms"));

            // Mod
            _bus.Add(new Parameter("mod.rate", 0, 20, 4, unit:"hz"));
            _bus.Add(new Parameter("mod.depth", 0, 100, 40, unit:"%"));
            _bus.Add(new Parameter("mod.mix", 0, 100, 30, unit:"%"));
            _bus.Add(new Parameter("mod.atk", 0, 500, 120, unit:"ms"));
            _bus.Add(new Parameter("mod.dec", 0, 500, 220, unit:"ms"));
            _bus.Add(new Parameter("mod.sus", 0, 100, 60, unit:"%"));
            _bus.Add(new Parameter("mod.rel", 0, 500, 240, unit:"ms"));

            // Tempo
            _bus.Add(new Parameter("tempo", 40, 200, 107, unit:"bpm"));
        }
    }
}
