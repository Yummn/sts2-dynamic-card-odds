using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using System.Reflection;

namespace DynamicCardOdds;

internal static partial class DcoLibraryUi
{
    private const string ButtonName = "DynamicCardOddsDisableButton";
    private const string OverlayName = "DynamicCardOddsDisabledOverlay";
    private const string TouchedMeta = "dynamic_card_odds_ui_touched";
    private const string WatcherName = "DynamicCardOddsLibraryWatcher";
    private static readonly Vector2 ButtonSize = new(176f, 56f);
    private static readonly string[] NonLibraryNeedles =
    [
        "NCombat", "CombatRoom", "Reward", "Merchant", "Shop", "InspectCard",
        "NCardPileScreen", "CardPileScreen", "DeckScreen", "DeckView",
        "DrawPile", "DiscardPile", "ExhaustPile", "MasterDeck"
    ];
    private static readonly FieldInfo? GridRowsField = AccessTools.Field(typeof(NCardGrid), "_cardRows");
    private static readonly FieldInfo? LibraryGridField = AccessTools.Field(typeof(NCardLibrary), "_grid");
    private static readonly FieldInfo? DescriptionLabelField = AccessTools.Field(typeof(NCard), "_descriptionLabel");
    private static readonly Dictionary<Type, PropertyInfo?> TextProperties = new();
    private static readonly Dictionary<Type, MethodInfo?> SetTextMethods = new();
    private static string? _lastToggleKey;
    private static ulong _lastToggleMsec;

    public static void EnsureInstalled()
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
                return;
            if (tree.Root.GetNodeOrNull<LibraryWatcher>(WatcherName) is not null)
                return;
            tree.Root.CallDeferred("add_child", new LibraryWatcher { Name = WatcherName });
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"[DynamicCardOdds] encyclopedia watcher install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void ApplyGrid(NCardLibraryGrid grid)
    {
        try
        {
            if (!IsLibraryContext(grid))
            {
                CleanupGrid(grid);
                return;
            }
            if (GridRowsField?.GetValue(grid) is not IEnumerable<List<NGridCardHolder>> rows)
                return;
            foreach (var row in rows)
                ApplyRow(grid, row);
        }
        catch { }
    }

    internal static void ApplyRow(NCardLibraryGrid grid, IEnumerable<NGridCardHolder>? row)
    {
        if (row is null)
            return;
        var valid = IsLibraryContext(grid);
        foreach (var holder in row)
        {
            try
            {
                var card = holder?.CardNode;
                if (card is null)
                    continue;
                if (!valid || holder is CanvasItem item && !item.Visible)
                    RemoveUi(card);
                else
                    ApplyCard(card, assumeLibrary: true);
            }
            catch { }
        }
    }

    private static void ApplyCard(NCard cardNode, bool assumeLibrary = false)
    {
        if (!assumeLibrary && !IsLibraryContext(cardNode))
        {
            RemoveUi(cardNode);
            return;
        }
        var model = cardNode.Model;
        if (!DcoAdaptiveOdds.ShouldShowCard(model))
        {
            RemoveUi(cardNode);
            return;
        }
        cardNode.SetMeta(TouchedMeta, Variant.From(true));
        AppendOddsLine(cardNode, model!);
        ApplyDisabledLook(cardNode, model!);
        EnsureButton(cardNode, model!);
    }

    private static void EnsureButton(NCard cardNode, CardModel model)
    {
        var disabled = DcoAdaptiveOdds.IsCardDisabled(model);
        var button = cardNode.GetNodeOrNull<Button>(ButtonName);
        if (button is null)
        {
            button = new Button
            {
                Name = ButtonName,
                Size = ButtonSize,
                CustomMinimumSize = ButtonSize,
                Position = new Vector2(28f, 48f),
                FocusMode = Control.FocusModeEnum.None,
                MouseFilter = Control.MouseFilterEnum.Stop,
                ZIndex = 42,
            };
            void Toggle() => ToggleFromButton(cardNode);
            button.ButtonDown += Toggle;
            button.Pressed += Toggle;
            button.GuiInput += input =>
            {
                if (input is InputEventScreenTouch touch && touch.Pressed)
                {
                    button.AcceptEvent();
                    ToggleFromButton(cardNode);
                }
                else if (input is InputEventMouseButton mouse && mouse.Pressed && mouse.ButtonIndex == MouseButton.Left)
                {
                    button.AcceptEvent();
                    ToggleFromButton(cardNode);
                }
            };
            cardNode.AddChild(button);
        }
        button.Text = disabled ? "启用出率" : "禁用出率";
        button.TooltipText = disabled
            ? "动态出率：重新启用这张机器人卡牌。无点数消耗、无数量上限。"
            : "动态出率：把这张机器人卡牌的奖励出率设为0。无点数消耗、无数量上限。";
        button.Size = ButtonSize;
        button.CustomMinimumSize = ButtonSize;
        button.Position = new Vector2(28f, 48f);
        button.Visible = true;
        ApplyButtonStyle(button, disabled);
    }

    private static void ToggleFromButton(NCard cardNode)
    {
        var model = cardNode.Model;
        if (model is null)
            return;
        var key = DcoAdaptiveOdds.CardKeyForPatch(model);
        var now = Time.GetTicksMsec();
        if (string.Equals(_lastToggleKey, key, StringComparison.Ordinal) && now - _lastToggleMsec < 350UL)
            return;
        _lastToggleKey = key;
        _lastToggleMsec = now;
        if (!DcoAdaptiveOdds.ToggleCardDisabled(model))
            return;
        ApplyCard(cardNode);
        MainFile.Logger.Info($"[DynamicCardOdds] encyclopedia toggle applied; disabled={DcoAdaptiveOdds.GetDisabledCount()} (unlimited).");
    }

    private static void ApplyButtonStyle(Button button, bool disabled)
    {
        var font = disabled ? new Color(0.82f, 1f, 0.72f) : new Color(1f, 0.78f, 0.67f);
        var background = disabled ? new Color(0.09f, 0.31f, 0.12f, 0.95f) : new Color(0.38f, 0.08f, 0.055f, 0.95f);
        var border = disabled ? new Color(0.43f, 0.82f, 0.31f) : new Color(0.92f, 0.31f, 0.20f);
        button.ClipText = true;
        button.AddThemeFontSizeOverride("font_size", 18);
        button.AddThemeColorOverride("font_color", font);
        button.AddThemeColorOverride("font_hover_color", font.Lightened(0.12f));
        button.AddThemeColorOverride("font_pressed_color", font.Darkened(0.18f));
        button.AddThemeStyleboxOverride("normal", MakeStyle(background, border, 3));
        button.AddThemeStyleboxOverride("hover", MakeStyle(background.Lightened(0.1f), border.Lightened(0.1f), 4));
        button.AddThemeStyleboxOverride("pressed", MakeStyle(background.Darkened(0.16f), border.Darkened(0.12f), 3));
    }

    private static StyleBoxFlat MakeStyle(Color background, Color border, int width) => new()
    {
        BgColor = background,
        BorderColor = border,
        BorderWidthLeft = width,
        BorderWidthTop = width,
        BorderWidthRight = width,
        BorderWidthBottom = width,
        CornerRadiusTopLeft = 12,
        CornerRadiusTopRight = 12,
        CornerRadiusBottomLeft = 12,
        CornerRadiusBottomRight = 12,
        ShadowColor = new Color(0f, 0f, 0f, 0.55f),
        ShadowSize = 5,
        ShadowOffset = new Vector2(0f, 3f),
        ContentMarginLeft = 10f,
        ContentMarginRight = 10f,
        ContentMarginTop = 5f,
        ContentMarginBottom = 5f,
    };

    private static void ApplyDisabledLook(NCard cardNode, CardModel model)
    {
        var disabled = DcoAdaptiveOdds.IsCardDisabled(model);
        try
        {
            var body = cardNode.Body;
            var overlay = body.GetNodeOrNull<ColorRect>(OverlayName);
            if (!disabled)
            {
                HideAndFree(overlay);
                body.Modulate = Colors.White;
                return;
            }
            if (overlay is null)
            {
                overlay = new ColorRect
                {
                    Name = OverlayName,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ZIndex = 39,
                    Color = new Color(0.035f, 0.035f, 0.04f, 0.42f),
                };
                body.AddChild(overlay);
            }
            overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            overlay.OffsetLeft = 0;
            overlay.OffsetTop = 0;
            overlay.OffsetRight = 0;
            overlay.OffsetBottom = 0;
            overlay.Visible = true;
            body.Modulate = new Color(0.68f, 0.68f, 0.68f, 0.98f);
        }
        catch { }
    }

    private static void AppendOddsLine(NCard cardNode, CardModel model)
    {
        var label = GetDescriptionLabel(cardNode);
        if (label is null)
            return;
        var current = GetLabelText(label);
        if (string.IsNullOrWhiteSpace(current))
            return;
        var cleaned = RemoveOddsLine(current);
        var insertion = $"\n[font_size=20]{DcoAdaptiveOdds.GetDisplayLine(model)}[/font_size]";
        var updated = cleaned.Contains("[/center]", StringComparison.Ordinal)
            ? cleaned.Replace("[/center]", insertion + "[/center]", StringComparison.Ordinal)
            : cleaned + insertion;
        if (!string.Equals(updated, current, StringComparison.Ordinal))
            SetLabelText(label, updated);
    }

    private static string RemoveOddsLine(string text)
    {
        foreach (var marker in new[] { "动态出率：", "åŠ¨æ€å‡ºçŽ‡", "?????" })
        {
            var index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                continue;
            var start = text.LastIndexOf("[font_size=", index, StringComparison.Ordinal);
            if (start < 0) start = Math.Max(0, text.LastIndexOf('\n', index));
            var end = text.IndexOf("[/font_size]", index, StringComparison.Ordinal);
            if (end >= 0) end += "[/font_size]".Length;
            else { end = text.IndexOf('\n', index); if (end < 0) end = text.Length; }
            return RemoveOddsLine(text.Remove(start, Math.Max(0, end - start)));
        }
        return text;
    }

    private static object? GetDescriptionLabel(NCard cardNode)
    {
        try { return DescriptionLabelField?.GetValue(cardNode); }
        catch { return null; }
    }

    private static string? GetLabelText(object label)
    {
        var type = label.GetType();
        lock (TextProperties)
        {
            if (!TextProperties.TryGetValue(type, out var property))
                TextProperties[type] = property = type.GetProperty("Text");
            try { return property?.GetValue(label) as string; }
            catch { return null; }
        }
    }

    private static void SetLabelText(object label, string text)
    {
        var type = label.GetType();
        lock (SetTextMethods)
        {
            if (!SetTextMethods.TryGetValue(type, out var method))
                SetTextMethods[type] = method = type.GetMethod("SetTextAutoSize", [typeof(string)]);
            try { method?.Invoke(label, [text]); } catch { }
        }
    }

    internal static void RemoveUi(NCard cardNode)
    {
        HideAndFree(cardNode.GetNodeOrNull<Button>(ButtonName));
        try
        {
            HideAndFree(cardNode.Body.GetNodeOrNull<ColorRect>(OverlayName));
            cardNode.Body.Modulate = Colors.White;
        }
        catch { }
        try
        {
            var label = GetDescriptionLabel(cardNode);
            var current = label is null ? null : GetLabelText(label);
            if (!string.IsNullOrEmpty(current))
            {
                var cleaned = RemoveOddsLine(current);
                if (!string.Equals(cleaned, current, StringComparison.Ordinal))
                    SetLabelText(label!, cleaned);
            }
        }
        catch { }
        try { cardNode.RemoveMeta(TouchedMeta); } catch { }
    }

    private static void HideAndFree(CanvasItem? item)
    {
        if (item is null || !GodotObject.IsInstanceValid(item))
            return;
        try { item.Visible = false; } catch { }
        try { if (item is Control control) control.MouseFilter = Control.MouseFilterEnum.Ignore; } catch { }
        try { item.QueueFree(); } catch { }
    }

    private static void CleanupGrid(NCardLibraryGrid grid)
    {
        try
        {
            if (GridRowsField?.GetValue(grid) is not IEnumerable<List<NGridCardHolder>> rows)
                return;
            foreach (var holder in rows.SelectMany(row => row))
                if (holder?.CardNode is { } card)
                    RemoveUi(card);
        }
        catch { }
    }

    internal static void CleanupAll()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree && tree.Root is { } root)
                CleanupRecursive(root, outsideOnly: false);
        }
        catch { }
    }

    internal static void CleanupOutsideLibrary()
    {
        try
        {
            if (Engine.GetMainLoop() is SceneTree tree && tree.Root is { } root)
                CleanupRecursive(root, outsideOnly: true);
        }
        catch { }
    }

    private static void CleanupRecursive(Node node, bool outsideOnly)
    {
        try
        {
            if (node is NCard card && card.HasMeta(TouchedMeta) && (!outsideOnly || !IsLibraryContext(card)))
                RemoveUi(card);
            foreach (var child in node.GetChildren())
                CleanupRecursive(child, outsideOnly);
        }
        catch { }
    }

    internal static bool IsLibraryContext(Node node)
    {
        try
        {
            var inspect = NGame.Instance?.InspectCardScreen;
            if (inspect is CanvasItem inspectItem && inspectItem.IsInsideTree() && inspectItem.Visible && inspectItem.IsVisibleInTree())
                return false;
        }
        catch { }
        NCardLibrary? library = null;
        NCardLibraryGrid? grid = null;
        for (var current = node; current != null; current = current.GetParent())
        {
            var typeName = current.GetType().FullName ?? current.GetType().Name;
            var nodeName = current.Name.ToString();
            if (NonLibraryNeedles.Any(value => typeName.Contains(value, StringComparison.OrdinalIgnoreCase) || nodeName.Contains(value, StringComparison.OrdinalIgnoreCase)))
                return false;
            grid ??= current as NCardLibraryGrid;
            if (current is NCardLibrary candidate)
            {
                library = candidate;
                break;
            }
        }
        if (library is null || grid is null || !IsVisible(library) || !IsVisible(grid) || !IsUnderCurrentScene(library))
            return false;
        return ReferenceEquals(GetGrid(library), grid);
    }

    private static bool IsVisible(Node node)
    {
        try { return node is CanvasItem item && item.IsInsideTree() && item.Visible && item.IsVisibleInTree(); }
        catch { return false; }
    }

    private static bool IsUnderCurrentScene(Node node)
    {
        try
        {
            if (Engine.GetMainLoop() is not SceneTree tree || tree.CurrentScene is null)
                return false;
            for (var current = node; current != null; current = current.GetParent())
                if (ReferenceEquals(current, tree.CurrentScene))
                    return true;
        }
        catch { }
        return false;
    }

    internal static NCardLibraryGrid? GetGrid(NCardLibrary library)
    {
        try { return LibraryGridField?.GetValue(library) as NCardLibraryGrid; }
        catch { return null; }
    }

    internal static void ScheduleRefresh(NCardLibrary library)
    {
        var timer = library.GetTree()?.CreateTimer(0.42);
        if (timer is null)
            return;
        timer.Timeout += () =>
        {
            if (GodotObject.IsInstanceValid(library) && GetGrid(library) is { } grid)
                ApplyGrid(grid);
        };
    }

    private sealed partial class LibraryWatcher : Node
    {
        private NCardLibrary? _library;
        private double _timer;
        private bool _active;

        public override void _Ready()
        {
            ProcessMode = ProcessModeEnum.Always;
            SetProcess(true);
        }

        public override void _Process(double delta)
        {
            _timer += delta;
            if (_timer < 0.20)
                return;
            _timer = 0;
            try
            {
                if (_library is null || !GodotObject.IsInstanceValid(_library) || !_library.IsInsideTree())
                    _library = FindLibrary();
                var grid = _library is null ? null : GetGrid(_library);
                var visible = grid is not null && IsLibraryContext(grid);
                if (!visible)
                {
                    if (_active)
                        CleanupAll();
                    _active = false;
                    _library = null;
                    return;
                }
                _active = true;
                CleanupOutsideLibrary();
                ApplyGrid(grid!);
            }
            catch { }
        }

        private static NCardLibrary? FindLibrary()
        {
            try
            {
                if (Engine.GetMainLoop() is not SceneTree tree || tree.Root is null)
                    return null;
                var stack = new Stack<Node>();
                stack.Push(tree.CurrentScene ?? tree.Root);
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    if (node is NCardLibrary library)
                        return library;
                    foreach (var child in node.GetChildren())
                        stack.Push(child);
                }
            }
            catch { }
            return null;
        }
    }
}

[HarmonyPatch(typeof(NCardLibrary), nameof(NCardLibrary.OnSubmenuOpened))]
internal static class DcoLibraryOpenedPatch
{
    private static void Postfix(NCardLibrary __instance)
    {
        if (DcoLibraryUi.GetGrid(__instance) is { } grid)
            DcoLibraryUi.ApplyGrid(grid);
    }
}

[HarmonyPatch(typeof(NCardLibrary), nameof(NCardLibrary.OnSubmenuClosed))]
internal static class DcoLibraryClosedPatch
{
    private static void Postfix() => DcoLibraryUi.CleanupAll();
}

[HarmonyPatch(typeof(NCardLibrary), "UpdateFilter")]
internal static class DcoLibraryFilterPatch
{
    private static void Postfix(NCardLibrary __instance) => DcoLibraryUi.ScheduleRefresh(__instance);
}

[HarmonyPatch(typeof(NCardLibraryGrid), "InitGrid")]
internal static class DcoLibraryGridInitPatch
{
    private static void Postfix(NCardLibraryGrid __instance) => DcoLibraryUi.ApplyGrid(__instance);
}

[HarmonyPatch(typeof(NCardLibraryGrid), "AssignCardsToRow")]
internal static class DcoLibraryGridAssignPatch
{
    private static void Postfix(NCardLibraryGrid __instance, List<NGridCardHolder> row) => DcoLibraryUi.ApplyRow(__instance, row.ToArray());
}

[HarmonyPatch(typeof(NCardLibraryGrid), "FilterCards", [typeof(Func<CardModel, bool>), typeof(List<SortingOrders>)])]
internal static class DcoLibraryFinalFilterPatch
{
    private static void Postfix(NCardLibraryGrid __instance) => DcoLibraryUi.ApplyGrid(__instance);
}

[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
internal static class DcoCardVisualScopePatch
{
    private static void Postfix(NCard __instance)
    {
        if (!DcoLibraryUi.IsLibraryContext(__instance))
            DcoLibraryUi.RemoveUi(__instance);
    }
}

[HarmonyPatch(typeof(NCard), "Reload")]
internal static class DcoCardReloadScopePatch
{
    private static void Postfix(NCard __instance)
    {
        if (!DcoLibraryUi.IsLibraryContext(__instance))
            DcoLibraryUi.RemoveUi(__instance);
    }
}
