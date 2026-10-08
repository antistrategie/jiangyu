using System;
using Il2CppInterop.Runtime;
using Il2CppMenace.Items;
using Il2CppMenace.UI.MissionResult;
using Jiangyu.Game.Ui;
using UnityEngine.UIElements;

namespace Jiangyu.Game.Ui.Components;

/// <summary>
/// A native item tile: the game's loot slot rendering an item (icon, stack, trade value),
/// with the game's slot hover, a frame around the tile while chosen, and
/// a chosen-count badge. Left-click and right-click adjust the count through
/// <see cref="OnAdjust(Action{int})"/>, or <see cref="OnAdjust(Action{int, bool})"/> to have a
/// held button repeat. It is an open wrapper: <see cref="Root"/> and <see cref="Badge"/>
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

    // Hold-to-repeat: a press waits HoldDelay before repeating at all, so an ordinary click never
    // repeats, then repeats at FirstInterval and closes on MinInterval by Decay each time. The
    // ticker runs at frame cadence and gates itself on the clock, which is how the interval can
    // shrink between repeats without rescheduling.
    private const float HoldDelay = 0.35f;
    private const float FirstInterval = 0.2f;
    private const float MinInterval = 0.035f;
    private const float Decay = 0.8f;
    private const long TickMs = 16;

    // The game's gold accent, distinct from the rarity colours a caller may frame the tile in.
    private static readonly UnityEngine.Color SelectedFrame = new(1f, 214f / 255f, 126f / 255f, 1f);
    private const float SelectedFrameWidth = 2f;

    private const string HoverSprite = "slot_hovered";
    private const float HoverSearchInterval = 2f;
    private static readonly UnityEngine.Color HoverTint = new(238f / 255f, 227f / 255f, 190f / 255f, 0.08f);

    private IVisualElementScheduledItem _holdTicker;
    private Action<int, bool> _onDelta;
    private int _holdDelta;
    private float _holdInterval;
    private float _holdNext;
    private int _chosen;
    private bool _tracksChosen;

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

        // The chosen state is a frame drawn here. The game's .slot-selected-border is a solid
        // notched backing (UI/Sprites/slot_selected) meant to sit behind a slot's contents, so
        // laid over the tile it covers the item icon.
        _selected = UiElementExtensions.FillOverlay();
        SetFrame(_selected, SelectedFrame, SelectedFrameWidth);
        _selected.SetVisible(false);
        Root.Add(_selected);

        Badge = new Label();
        Badge.pickingMode = PickingMode.Ignore;
        Badge.style.position = new StyleEnum<Position>(Position.Absolute);
        Badge.style.top = new StyleLength(-4f);
        Badge.style.right = new StyleLength(-4f);
        Badge.SetVisible(false);
        Root.Add(Badge);

        WireSlotHover();
    }

    // The game's slot hover: the sprite and tint the game's tab_button and event_choice_button
    // layouts give their Hover element. A loot slot has no hover of its own. The sprite is not
    // under Resources, so it is found among the loaded sprites, and painted on each hover so a
    // tile built before it loaded, or kept after it unloaded, still shows it. If it is missing,
    // the hover is the tint alone.
    private void WireSlotHover()
    {
        var hover = UiElementExtensions.FillOverlay();
        hover.SetVisible(false);
        Root.Insert(1, hover);
        Root.RegisterCallback<PointerEnterEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerEnterEvent>>(
            (Action<PointerEnterEvent>)(_ =>
            {
                PaintHover(hover);
                hover.SetVisible(true);
            })));
        Root.RegisterCallback<PointerLeaveEvent>(DelegateSupport.ConvertDelegate<EventCallback<PointerLeaveEvent>>(
            (Action<PointerLeaveEvent>)(_ => hover.SetVisible(false))));
    }

    private static void PaintHover(VisualElement hover)
    {
        try
        {
            var sprite = SlotHoverSprite();
            if (sprite != null)
            {
                hover.style.backgroundImage = new StyleBackground(sprite);
                hover.style.unityBackgroundImageTintColor = new StyleColor(HoverTint);
                hover.style.backgroundColor = new StyleColor(UnityEngine.Color.clear);
            }
            else
            {
                hover.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                hover.style.backgroundColor = new StyleColor(HoverTint);
            }
        }
        catch { }
    }

    private static UnityEngine.Sprite _hoverSprite;
    private static float _hoverSearchAfter;

    // Searched while unset or destroyed (a scene change can unload it), with a missed search not
    // repeated for HoverSearchInterval: the search walks every loaded sprite.
    private static UnityEngine.Sprite SlotHoverSprite()
    {
        if (_hoverSprite != null)
            return _hoverSprite;
        var now = UnityEngine.Time.unscaledTime;
        if (now < _hoverSearchAfter)
            return null;
        try
        {
            foreach (var obj in UnityEngine.Resources.FindObjectsOfTypeAll(Il2CppType.Of<UnityEngine.Sprite>()))
            {
                if (obj != null && obj.name == HoverSprite)
                {
                    _hoverSprite = obj.Cast<UnityEngine.Sprite>();
                    return _hoverSprite;
                }
            }
        }
        catch { }
        _hoverSearchAfter = now + HoverSearchInterval;
        return null;
    }

    /// <summary>Left-click calls <paramref name="onDelta"/> with +1, right-click with -1.</summary>
    public ItemTile OnAdjust(Action<int> onDelta)
        => onDelta == null ? this : Wire((delta, _) => onDelta(delta), repeats: false);

    /// <summary>
    /// Left-click calls <paramref name="onDelta"/> with +1, right-click with -1, and holding the
    /// button down repeats that: nothing for a moment, so an ordinary click stays a single step,
    /// then repeats that start slow and accelerate for as long as the button is held. The second
    /// argument is false for the press and true for every repeat, so a caller can keep click
    /// sounds and other one-shot feedback on the press alone. Repeats stop on release, on the
    /// pointer leaving the tile, and once the count last reported through <see cref="SetChosen"/>
    /// has run out of room in the direction being held.
    /// </summary>
    public ItemTile OnAdjust(Action<int, bool> onDelta)
        => onDelta == null ? this : Wire(onDelta, repeats: true);

    private ItemTile Wire(Action<int, bool> onDelta, bool repeats)
    {
        _onDelta = onDelta;
        Root.RegisterCallback<PointerDownEvent>(
            DelegateSupport.ConvertDelegate<EventCallback<PointerDownEvent>>(
                (Action<PointerDownEvent>)(evt =>
                {
                    var delta = evt.button == 1 ? -1 : evt.button == 0 ? 1 : 0;
                    if (delta == 0)
                        return;
                    onDelta(delta, false);
                    if (repeats)
                        BeginHold(delta);
                })),
            TrickleDown.TrickleDown);

        if (!repeats)
            return this;

        // Release, leaving the tile, a cancelled press and the tile going away all end the hold.
        // Without the leave and detach cases a press that wanders off or a modal that closes
        // mid-hold would leave the ticker running against a tile nobody is pressing.
        StopHoldOn<PointerUpEvent>();
        StopHoldOn<PointerLeaveEvent>();
        StopHoldOn<PointerCancelEvent>();
        StopHoldOn<DetachFromPanelEvent>();
        return this;
    }

    private void StopHoldOn<TEvent>() where TEvent : EventBase<TEvent>, new()
        => Root.RegisterCallback<TEvent>(
            DelegateSupport.ConvertDelegate<EventCallback<TEvent>>((Action<TEvent>)(_ => EndHold())));

    private void BeginHold(int delta)
    {
        _holdDelta = delta;
        _holdInterval = FirstInterval;
        _holdNext = UnityEngine.Time.unscaledTime + HoldDelay;
        _holdTicker ??= Root.schedule
            .Execute(DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(HoldTick))
            .Every(TickMs);
        _holdTicker.Resume();
    }

    private void EndHold()
    {
        _holdDelta = 0;
        try { _holdTicker?.Pause(); }
        catch { }
    }

    private void HoldTick()
    {
        if (_holdDelta == 0)
            return;
        var now = UnityEngine.Time.unscaledTime;
        if (now < _holdNext)
            return;
        // A caller that reports its count back to us lets the repeat stop the moment it saturates,
        // rather than spinning against a clamp for as long as the button is down.
        if (_tracksChosen && (_holdDelta > 0 ? _chosen >= Owned : _chosen <= 0))
        {
            EndHold();
            return;
        }

        try { _onDelta?.Invoke(_holdDelta, true); }
        catch { EndHold(); return; }

        _holdInterval = Math.Max(MinInterval, _holdInterval * Decay);
        _holdNext = now + _holdInterval;
    }

    /// <summary>Reflect a chosen count: the selected border and the badge text.</summary>
    public void SetChosen(int count)
    {
        _chosen = count;
        _tracksChosen = true;
        _selected.SetVisible(count > 0);
        Badge.text = count > 0 ? "x" + count : "";
        Badge.SetVisible(count > 0);
    }

    private static void SetFrame(VisualElement element, UnityEngine.Color colour, float width)
    {
        var c = new StyleColor(colour);
        element.style.borderTopColor = c;
        element.style.borderBottomColor = c;
        element.style.borderLeftColor = c;
        element.style.borderRightColor = c;
        var w = new StyleFloat(width);
        element.style.borderTopWidth = w;
        element.style.borderBottomWidth = w;
        element.style.borderLeftWidth = w;
        element.style.borderRightWidth = w;
    }
}
