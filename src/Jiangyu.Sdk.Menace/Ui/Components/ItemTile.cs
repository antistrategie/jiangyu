using System;
using Il2CppInterop.Runtime;
using Il2CppMenace.Items;
using Il2CppMenace.UI.MissionResult;
using Jiangyu.Game.Ui;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// A native item tile: the game's loot slot rendering an item (icon, stack, trade value),
/// with native hover, the game's <c>.slot-selected-border</c> highlight while chosen, and
/// a chosen-count badge. Left-click and right-click adjust the count through
/// <see cref="OnAdjust"/>. It is an open wrapper: <see cref="Root"/> and <see cref="Badge"/>
/// are real elements to restyle or extend.
/// </summary>
public sealed class ItemTile
{
    /// <summary>The tile element. Inject this.</summary>
    public VisualElement Root { get; }

    /// <summary>The chosen-count badge (hidden at zero). Restyle it if you like.</summary>
    public Label Badge { get; }

    /// <summary>How many the player owns, the natural clamp ceiling for selection.</summary>
    public int Owned { get; }

    private readonly VisualElement _selected;

    public ItemTile(BaseItemTemplate item, int owned)
    {
        Owned = owned;
        Root = new VisualElement();

        try
        {
            var slot = new MissionResultLootSlot();
            slot.Init(item, owned);
            Root.Add(slot);
        }
        catch { }

        _selected = UiElementExtensions.FillOverlay();
        _selected.AddToClassList("slot-selected-border");
        _selected.SetVisible(false);
        Root.Add(_selected);

        Badge = new Label();
        Badge.pickingMode = PickingMode.Ignore;
        Badge.style.position = new StyleEnum<Position>(Position.Absolute);
        Badge.style.top = new StyleLength(-4f);
        Badge.style.right = new StyleLength(-4f);
        Badge.SetVisible(false);
        Root.Add(Badge);

        Root.WireNativeHover();
    }

    /// <summary>Left-click calls <paramref name="onDelta"/> with +1, right-click with -1.</summary>
    public ItemTile OnAdjust(Action<int> onDelta)
    {
        if (onDelta != null)
            Root.RegisterCallback<PointerDownEvent>(
                DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                    (Action<PointerDownEvent>)(evt =>
                    {
                        if (evt.button == 1)
                            onDelta(-1);
                        else if (evt.button == 0)
                            onDelta(1);
                    })),
                TrickleDown.TrickleDown);
        return this;
    }

    /// <summary>Reflect a chosen count: the selected border and the badge text.</summary>
    public void SetChosen(int count)
    {
        _selected.SetVisible(count > 0);
        Badge.text = count > 0 ? "x" + count : "";
        Badge.SetVisible(count > 0);
    }

}
