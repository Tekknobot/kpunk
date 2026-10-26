using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZPunk.DAW
{
    /// <summary>
    /// Minimal white icon language for ZPUNK's ARRANGE/CREATE command buttons.
    /// The artwork is built once as tiny anti-aliased RGBA textures at runtime:
    /// no fonts, SVG package, import settings or platform-dependent glyphs.
    /// Visual-only; buttons retain their original click callbacks and hit areas.
    /// </summary>
    internal static class ZPunkUIIcons
    {
        private const int Size = 64;
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        private static readonly Color White = new Color(.92f, .95f, .98f);

        internal static void Apply(Button button, string iconKey, string caption,
            float iconSize = 19f, bool stacked = true)
        {
            if (button == null) return;
            button.text = string.Empty;
            button.Clear();
            button.tooltip = caption;
            bool hasCaption = !string.IsNullOrEmpty(caption);
            button.style.flexDirection = stacked ? FlexDirection.Column : FlexDirection.Row;
            button.style.alignItems = Align.Center;
            button.style.justifyContent = Justify.Center;
            // The label's glyph ascenders/descenders make a mathematically
            // centred stack look high. Bias the WHOLE stack down slightly so the
            // visible gap above the icon matches the gap below the caption.
            button.style.paddingTop = stacked && hasCaption ? 7 : 2;
            button.style.paddingBottom = stacked && hasCaption ? 1 : 2;
            button.style.whiteSpace = WhiteSpace.NoWrap;
            var icon = new Image
            {
                name = "ZPunkIcon",
                image = Texture(iconKey),
                scaleMode = ScaleMode.ScaleToFit,
                pickingMode = PickingMode.Ignore
            };
            icon.style.width = iconSize;
            icon.style.height = iconSize;
            icon.style.flexShrink = 0;
            icon.style.marginRight = stacked ? 0 : 5;
            // No independent icon offset: centre the icon and caption as a unit.
            button.Add(icon);
            if (hasCaption)
            {
                var label = new Label(caption) { name = "ZPunkIconCaption", pickingMode = PickingMode.Ignore };
                label.style.color = White;
                label.style.fontSize = stacked ? 10 : 11;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.NoWrap;
                label.style.flexShrink = 0;
                if (stacked) label.style.marginTop = 2;
                button.Add(label);
            }
        }

        internal static void SetCaption(Button button, string caption)
        {
            if (button == null) return;
            var label = button.Q<Label>("ZPunkIconCaption");
            if (label != null) label.text = caption;
            button.tooltip = caption;
        }

        private static Texture2D Texture(string key)
        {
            Texture2D texture;
            if (Cache.TryGetValue(key, out texture) && texture != null) return texture;
            var drawing = new Drawing();
            switch (key)
            {
                case "plus": drawing.Line(16, 6, 16, 26); drawing.Line(6, 16, 26, 16); break;
                case "minus": drawing.Line(6, 16, 26, 16); break;
                case "left": drawing.Line(20, 6, 10, 16); drawing.Line(10, 16, 20, 26); break;
                case "right": drawing.Line(12, 6, 22, 16); drawing.Line(22, 16, 12, 26); break;
                case "up": drawing.Line(16, 5, 16, 27); drawing.Line(7, 14, 16, 5); drawing.Line(25, 14, 16, 5); break;
                case "down": drawing.Line(16, 5, 16, 27); drawing.Line(7, 18, 16, 27); drawing.Line(25, 18, 16, 27); break;
                case "edit":
                    drawing.Line(7, 7, 21, 21, 2.7f);
                    drawing.Line(10, 5, 24, 19, 2.7f);
                    drawing.Line(21, 21, 24, 19);
                    drawing.Line(7, 7, 5, 4); drawing.Line(5, 4, 10, 5);
                    drawing.Line(20, 22, 23, 25); drawing.Line(23, 25, 26, 22);
                    break;
                case "copy":
                    drawing.Box(5, 5, 20, 20); drawing.Box(12, 12, 27, 27);
                    break;
                case "delete":
                    drawing.Line(8, 24, 24, 24);
                    drawing.Line(12, 27, 20, 27); drawing.Line(16, 24, 16, 27);
                    drawing.Box(10, 5, 22, 22);
                    drawing.Line(14, 9, 14, 18); drawing.Line(18, 9, 18, 18);
                    break;
                case "zoom-in":
                case "zoom-out":
                    drawing.Circle(14, 18, 9);
                    drawing.Line(20, 11, 28, 3, 2.8f);
                    drawing.Line(9, 18, 19, 18);
                    if (key == "zoom-in") drawing.Line(14, 13, 14, 23);
                    break;
                case "magnet":
                    // U-shaped magnet, with two distinguishable pole caps.
                    drawing.Line(7, 25, 7, 13, 3);
                    drawing.Arc(16, 13, 9, 180, 360, 12, 3);
                    drawing.Line(25, 13, 25, 25, 3);
                    drawing.Line(6, 25, 10, 25, 3); drawing.Line(23, 25, 27, 25, 3);
                    drawing.Line(7, 20, 10, 20); drawing.Line(22, 20, 25, 20);
                    break;
                case "loop":
                    drawing.Line(7, 21, 22, 21);
                    drawing.Line(22, 21, 19, 25); drawing.Line(22, 21, 19, 17);
                    drawing.Line(25, 11, 10, 11);
                    drawing.Line(10, 11, 13, 15); drawing.Line(10, 11, 13, 7);
                    drawing.Arc(22, 16, 5, -90, 90, 8);
                    drawing.Arc(10, 16, 5, 90, 270, 8);
                    break;
                case "spark":
                    drawing.Line(16, 4, 19, 12); drawing.Line(19, 12, 27, 16);
                    drawing.Line(27, 16, 19, 20); drawing.Line(19, 20, 16, 28);
                    drawing.Line(16, 28, 13, 20); drawing.Line(13, 20, 5, 16);
                    drawing.Line(5, 16, 13, 12); drawing.Line(13, 12, 16, 4);
                    drawing.Line(26, 25, 26, 30, 1.6f); drawing.Line(23.5f, 27.5f, 28.5f, 27.5f, 1.6f);
                    break;
                case "save":
                    drawing.Box(6, 5, 26, 27);
                    drawing.Box(11, 19, 22, 27);
                    drawing.Box(11, 5, 22, 12);
                    drawing.Line(19, 6, 19, 11);
                    break;
                default: drawing.Line(6, 16, 26, 16); break;
            }
            var pixels = drawing.Pixels();
            texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            texture.name = "ZPunk Icon " + key;
            texture.hideFlags = HideFlags.DontSave;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            Cache[key] = texture;
            return texture;
        }

        private sealed class Drawing
        {
            private readonly byte[] alpha = new byte[Size * Size];
            internal void Line(float x0, float y0, float x1, float y1, float width = 2.4f)
            {
                float dx = x1 - x0, dy = y1 - y0;
                float lengthSquared = dx * dx + dy * dy;
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        float px = (x + .5f) * 32f / Size;
                        float py = (y + .5f) * 32f / Size;
                        float t = lengthSquared > 0 ? Mathf.Clamp01(((px - x0) * dx + (py - y0) * dy) / lengthSquared) : 0;
                        float xx = x0 + t * dx, yy = y0 + t * dy;
                        float distance = Mathf.Sqrt((px - xx) * (px - xx) + (py - yy) * (py - yy));
                        byte coverage = (byte)(255f * Mathf.Clamp01((width * .5f + .4f - distance) / .8f));
                        int index = y * Size + x;
                        if (coverage > alpha[index]) alpha[index] = coverage;
                    }
            }
            internal void Box(float left, float bottom, float right, float top)
            {
                Line(left, bottom, right, bottom); Line(right, bottom, right, top);
                Line(right, top, left, top); Line(left, top, left, bottom);
            }
            internal void Arc(float cx, float cy, float radius, float degreesStart, float degreesEnd,
                int segments, float width = 2.4f)
            {
                float a = degreesStart * Mathf.Deg2Rad;
                float px = cx + radius * Mathf.Cos(a), py = cy + radius * Mathf.Sin(a);
                for (int i = 1; i <= segments; i++)
                {
                    a = Mathf.Lerp(degreesStart, degreesEnd, i / (float)segments) * Mathf.Deg2Rad;
                    float x = cx + radius * Mathf.Cos(a), y = cy + radius * Mathf.Sin(a);
                    Line(px, py, x, y, width);
                    px = x; py = y;
                }
            }
            internal void Circle(float cx, float cy, float radius) { Arc(cx, cy, radius, 0, 360, 32); }
            internal Color32[] Pixels()
            {
                var pixels = new Color32[Size * Size];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(236, 242, 250, alpha[i]);
                return pixels;
            }
        }
    }
}
