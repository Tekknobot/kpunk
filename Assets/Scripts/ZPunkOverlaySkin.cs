using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Compact panel styling shared by the ZPUNK project library and sample browser.
/// Uses UI Toolkit point sizes so PanelSettings can scale it for phones/tablets.
/// Visual-only: no changes to save, import or playback services.
/// </summary>
internal static class ZPunkOverlaySkin
{
    internal static readonly Color Ink = new Color(.91f,.95f,.98f);
    internal static readonly Color Muted = new Color(.61f,.71f,.79f);
    internal static readonly Color Accent = new Color(.28f,.84f,.73f);
    internal static readonly Color Surface = new Color(.066f,.095f,.137f);
    internal static readonly Color Panel = new Color(.095f,.133f,.186f);
    internal static readonly Color Field = new Color(.065f,.105f,.155f);
    internal static readonly Color Line = new Color(.22f,.31f,.40f);

    internal static void Card(VisualElement card, float maxWidth, float maxHeight)
    {
        card.style.position = Position.Relative;
        card.style.left = StyleKeyword.Auto;
        card.style.right = StyleKeyword.Auto;
        card.style.top = StyleKeyword.Auto;
        card.style.bottom = StyleKeyword.Auto;
        card.style.width = Length.Percent(100);
        card.style.maxWidth = maxWidth;
        card.style.height = Length.Percent(92);
        card.style.maxHeight = maxHeight;
        card.style.minHeight = 0;
        card.style.minWidth = 0;
        card.style.flexGrow = 0;
        card.style.flexShrink = 1;
        card.style.flexDirection = FlexDirection.Column;
        card.style.overflow = Overflow.Hidden;
        card.style.paddingLeft = 14;
        card.style.paddingRight = 14;
        card.style.paddingTop = 15;
        card.style.paddingBottom = 13;
        card.style.backgroundColor = Surface;
        card.style.borderTopWidth = 1;
        card.style.borderRightWidth = 1;
        card.style.borderBottomWidth = 1;
        card.style.borderLeftWidth = 1;
        card.style.borderTopColor = Line;
        card.style.borderRightColor = Line;
        card.style.borderBottomColor = Line;
        card.style.borderLeftColor = Line;
        card.style.borderTopLeftRadius = 10;
        card.style.borderTopRightRadius = 10;
        card.style.borderBottomLeftRadius = 10;
        card.style.borderBottomRightRadius = 10;
    }

    internal static void Heading(Label heading)
    {
        heading.style.fontSize = 19;
        heading.style.unityFontStyleAndWeight = FontStyle.Bold;
        heading.style.color = Ink;
        heading.style.whiteSpace = WhiteSpace.NoWrap;
        heading.style.textOverflow = TextOverflow.Ellipsis;
        heading.style.overflow = Overflow.Hidden;
        heading.style.flexShrink = 1;
        heading.style.minWidth = 0;
    }

    internal static void Button(Button button, bool primary = false, bool danger = false)
    {
        button.style.height = 44;
        button.style.minHeight = 44;
        button.style.fontSize = 12;
        button.style.unityFontStyleAndWeight = FontStyle.Normal;
        button.style.color = danger ? new Color(1f,.70f,.69f) : primary ? new Color(.07f,.16f,.18f) : Ink;
        button.style.backgroundColor = primary ? Accent : Panel;
        button.style.borderTopWidth = 1;
        button.style.borderRightWidth = 1;
        button.style.borderBottomWidth = 1;
        button.style.borderLeftWidth = 1;
        Color outline = primary ? Accent : danger ? new Color(.40f,.24f,.27f) : Line;
        button.style.borderTopColor = outline;
        button.style.borderRightColor = outline;
        button.style.borderBottomColor = outline;
        button.style.borderLeftColor = outline;
        button.style.borderTopLeftRadius = 6;
        button.style.borderTopRightRadius = 6;
        button.style.borderBottomLeftRadius = 6;
        button.style.borderBottomRightRadius = 6;
        button.style.paddingLeft = 7;
        button.style.paddingRight = 7;
        button.style.marginLeft = 0;
        button.style.marginRight = 0;
    }

    // ScrollView's native Android slider retains space for its arrow controls
    // even when those buttons are visually hidden. Use a separate, fixed-height
    // scrollbar alongside the ScrollView so the rail covers the *entire* list.
    // This also keeps native touch/flick/wheel scrolling and virtualization.
    internal static void Scroll(ScrollView scroll)
    {
        scroll.mode = ScrollViewMode.Vertical;
        scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
        scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
        scroll.style.flexGrow = 1;
        scroll.style.flexShrink = 1;
        scroll.style.minHeight = 0;
        scroll.style.minWidth = 0;
        scroll.style.width = StyleKeyword.Auto;
        scroll.style.paddingRight = 0;
        scroll.contentContainer.style.minWidth = 0;
        scroll.contentContainer.style.paddingRight = 3;
    }

    /// <summary>
    /// Hosts a ScrollView and an independent, draggable rail. The rail spans the
    /// actual viewport top-to-bottom; its thumb represents visible/total content.
    /// Avoids UI Toolkit's built-in slider inset/arrow spacing on Android.
    /// </summary>
    internal static VisualElement ScrollWithRail(ScrollView scroll)
    {
        Scroll(scroll);
        var host = new VisualElement { name = "ZPunkScrollViewport" };
        host.style.flexDirection = FlexDirection.Row;
        host.style.alignItems = Align.Stretch;
        host.style.flexGrow = 1;
        host.style.flexShrink = 1;
        host.style.minWidth = 0;
        host.style.minHeight = 0;
        host.style.width = Length.Percent(100);
        host.style.overflow = Overflow.Hidden;
        host.Add(scroll);

        var rail = new VisualElement { name = "ZPunkScrollbarRail" };
        rail.style.position = Position.Relative;
        rail.style.width = 12;
        rail.style.minWidth = 12;
        rail.style.maxWidth = 12;
        rail.style.flexShrink = 0;
        rail.style.alignSelf = Align.Stretch;
        rail.style.marginLeft = 6;
        rail.style.marginRight = 0;
        rail.style.backgroundColor = new Color(.048f, .076f, .109f);
        rail.style.borderTopLeftRadius = 4;
        rail.style.borderTopRightRadius = 4;
        rail.style.borderBottomLeftRadius = 4;
        rail.style.borderBottomRightRadius = 4;

        var thumb = new VisualElement { name = "ZPunkScrollbarThumb" };
        thumb.style.position = Position.Absolute;
        thumb.style.left = 2;
        thumb.style.right = 2;
        thumb.style.top = 0;
        thumb.style.height = 40;
        thumb.style.backgroundColor = new Color(.29f, .57f, .60f);
        thumb.style.borderTopLeftRadius = 4;
        thumb.style.borderTopRightRadius = 4;
        thumb.style.borderBottomLeftRadius = 4;
        thumb.style.borderBottomRightRadius = 4;
        thumb.pickingMode = PickingMode.Ignore; // Rail handles track and thumb drags.
        rail.Add(thumb);
        host.Add(rail);

        float thumbHeight = 0f;
        float maxOffset = 0f;
        System.Action update = () =>
        {
            if (scroll.panel == null) return;
            float railHeight = rail.resolvedStyle.height;
            float viewHeight = scroll.contentViewport.resolvedStyle.height;
            float contentHeight = scroll.contentContainer.resolvedStyle.height;
            if (railHeight <= 0f || viewHeight <= 0f || contentHeight <= 0f) return;

            maxOffset = Mathf.Max(0f, contentHeight - viewHeight);
            thumbHeight = maxOffset <= .5f
                ? railHeight
                : Mathf.Clamp(railHeight * viewHeight / contentHeight, 32f, railHeight);
            float travel = Mathf.Max(0f, railHeight - thumbHeight);
            float fraction = maxOffset > 0f ? Mathf.Clamp01(scroll.scrollOffset.y / maxOffset) : 0f;
            thumb.style.height = thumbHeight;
            thumb.style.top = fraction * travel;
            thumb.style.opacity = maxOffset > .5f ? 1f : .45f;
        };

        bool dragging = false;
        float grabOffset = 0f;
        System.Action<float> jumpTo = y =>
        {
            float travel = Mathf.Max(0f, rail.resolvedStyle.height - thumbHeight);
            if (travel <= .01f || maxOffset <= .01f) return;
            float progress = Mathf.Clamp01((y - grabOffset) / travel);
            scroll.scrollOffset = new Vector2(scroll.scrollOffset.x, progress * maxOffset);
            update();
        };
        rail.RegisterCallback<PointerDownEvent>(evt =>
        {
            update();
            float y = rail.WorldToLocal(evt.position).y;
            float top = thumb.resolvedStyle.top;
            grabOffset = y >= top && y <= top + thumbHeight ? y - top : thumbHeight * .5f;
            dragging = true;
            rail.CapturePointer(evt.pointerId);
            jumpTo(y);
            evt.StopPropagation();
        });
        rail.RegisterCallback<PointerMoveEvent>(evt =>
        {
            if (!dragging || !rail.HasPointerCapture(evt.pointerId)) return;
            jumpTo(rail.WorldToLocal(evt.position).y);
            evt.StopPropagation();
        });
        rail.RegisterCallback<PointerUpEvent>(evt =>
        {
            if (!dragging) return;
            dragging = false;
            if (rail.HasPointerCapture(evt.pointerId)) rail.ReleasePointer(evt.pointerId);
            evt.StopPropagation();
        });
        rail.RegisterCallback<PointerCaptureOutEvent>(_ => dragging = false);

        rail.RegisterCallback<GeometryChangedEvent>(_ => update());
        scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ => update());
        scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => update());
        scroll.verticalScroller.valueChanged += _ => update();
        // Touch inertia and async library refreshes can change the offset after
        // callbacks have fired. This only sets thumb position, never rebuilds UI.
        rail.schedule.Execute(() => update()).Every(75);
        return host;
    }

    internal static void TextInput(TextField field)
    {
        field.style.color = Ink;
        field.style.backgroundColor = Field;
        field.style.fontSize = 14;
        field.style.height = 46;
        field.style.minHeight = 46;
        field.style.minWidth = 0;
        field.style.paddingLeft = 9;
        field.style.paddingRight = 9;
        field.style.borderTopLeftRadius = 6;
        field.style.borderTopRightRadius = 6;
        field.style.borderBottomLeftRadius = 6;
        field.style.borderBottomRightRadius = 6;
        field.style.borderTopWidth = 1;
        field.style.borderRightWidth = 1;
        field.style.borderBottomWidth = 1;
        field.style.borderLeftWidth = 1;
        field.style.borderTopColor = Line;
        field.style.borderRightColor = Line;
        field.style.borderBottomColor = Line;
        field.style.borderLeftColor = Line;
        ApplyTextInputColors(field);
        field.RegisterCallback<AttachToPanelEvent>(_ =>
        {
            ApplyTextInputColors(field);
            field.schedule.Execute(() => ApplyTextInputColors(field)).ExecuteLater(0);
        });
    }

    private static void ApplyTextInputColors(TextField field)
    {
        if (field == null) return;
        field.style.backgroundColor = Field;
        field.style.color = Ink;
        // The default Unity input background is white on some Android builds.
        // The input is nested under TextField, so styling TextField alone cannot
        // override the actual editable element's background or font color.
        foreach (var child in field.Query<VisualElement>().ToList())
        {
            if (child == field || child is Label) continue;
            child.style.backgroundColor = Color.clear;
            child.style.color = Ink;
            child.style.fontSize = 14;
        }
    }
}
