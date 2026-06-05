# Game UI

[Hooks](./hooks) react to moments. [Template types](./template-types) change the game's data. [Game verbs](./verbs) read and command the live game. **Game UI** is the surface for the screen itself: it injects your own elements into the game's existing UI, so a custom bar, panel, or modal sits inside a native screen and looks like it belongs.

It lives in the `Jiangyu.Game` namespace, alongside the verbs, as plain static calls on `UI`:

```csharp
using Jiangyu.Game;
using UnityEngine.UIElements;
```

Like verbs, this is code, so a UI mod has a [code project](/sdk/#the-code-project). You author the markup as [UXML](#authoring-ui-in-uxml) and place it with a selector and a target.

## The shape of an injection

Every injection answers three questions: **where** to put it, **what** to add, and **how** to wire it up.

```csharp
UI.Inject(
    UiTarget.Screen<ArmoryUIScreen>().After(UiSelector.Name("HealthBar")),  // where
    "relationship_bar",                                                      // what (a bundled UXML)
    element => element.Q<Label>("Value").text = "12");                       // how (optional)
```

`UI.Inject` returns a `UiInjection` handle you keep to [refresh or remove](#keeping-an-injection-current) it. The loader re-applies it for you when the screen changes, so you set it up once and never re-inject by hand.

## Where to inject

A `UiTarget` picks the screen or dialog to inject into.

| Factory | Targets |
| --- | --- |
| `UiTarget.Screen<TScreen>()` | the active screen, only when its type is `TScreen` |
| `UiTarget.ActiveScreen()` | whichever screen is active, any type |
| `UiTarget.Dialog<TDialog>()` | the open dialog, only when its type is `TDialog` |
| `UiTarget.Overlay()` | a full-screen layer above the active screen, for a [modal](#modals-and-overlays) |

By default the element is appended to the end of the target. Chain a placement to put it somewhere precise:

| Placement | Effect |
| --- | --- |
| `.After(selector)` | insert right after the matched anchor, as its sibling |
| `.Before(selector)` | insert right before the matched anchor |
| `.AppendTo(selector)` | append inside the matched container |
| `.Each(selector)` | inject once into every element the selector matches, not once into the root |

`.Each` is how you add something to a repeated element, a row in a list or a unit slot: the injection lands in each match and re-lands as the list rebuilds.

## What to match

A `UiSelector` names an element in the live tree, for an anchor, a container, or an `.Each` scope.

| Selector | Matches |
| --- | --- |
| `UiSelector.Name("HealthBar")` | the element whose name is `HealthBar` |
| `UiSelector.Class("stat-bar")` | any element carrying the USS class `stat-bar` |
| `UiSelector.Type<T>()` | any element whose type is `T`, or a subclass |
| `UiSelector.TypeName("ArmoryUnitSelectSlot")` | any element whose type name is the given string |
| `a.And(b)` | only elements matching both `a` and `b` |

Reach for `TypeName` when the game type has no compile-time wrapper to use with `Type<T>`. To discover the names, classes, and types a screen actually uses, open the [UI Inspector](/studio#ui-inspector) in Studio and capture the live tree, then copy a selector straight off a node.

## What to add

Three ways to supply the element, each an overload of `Inject` and `InjectEach`:

| Content | Call |
| --- | --- |
| a bundled UXML, by name | `UI.Inject(target, "my_panel")` |
| a loaded `VisualTreeAsset` | `UI.Inject(target, asset)` |
| an element built in code | `UI.Inject(target, () => new Label("hi"))` |

The string form resolves the name against your own mod's bundled assets (see [Authoring UI in UXML](#authoring-ui-in-uxml)). Authoring in UXML is the path to prefer: the markup and its stylesheet live as assets rather than buried in C#.

### Binding

Every overload takes an optional `bind` callback that runs after the element is placed, where you fill it in, set text, wire a click, or position it:

```csharp
UI.Inject(target, "give_gift_button", element =>
{
    element.Q<Button>().clicked += OpenGiftModal;
});
```

`UI.Find(root, selector)` and `UI.FindAll(root, selector)` query within an element. Use them in `bind` to reach neighbours.

### Injecting into every match

`InjectEach` places one copy per element matched by `.Each`, and its `bind` receives a second argument, the matched **scope**, so you can read data off the element you injected beside:

```csharp
UI.InjectEach(
    UiTarget.ActiveScreen().Each(UiSelector.TypeName("ArmoryUnitSelectSlot")),
    "relationship_bar",
    (bar, slot) =>
    {
        var anchor = UI.Find(slot, UiSelector.Name("HealthBar"));
        if (anchor != null)
            bar.StackAfter(anchor, gap: 2f);
    });
```

## Matching the game's look

Two extensions make an injected element sit naturally beside a native one:

- `target.MatchStyle(reference)` copies every USS class off `reference` onto `target`, so it inherits the same stylesheet rules. Use it when the neighbour is styled by class.
- `target.StackAfter(reference, gap)` positions `target` absolutely one row below `reference`, matching its left edge, width, and height. Use it when the neighbour carries no classes and is positioned in code, as many game bars are. It reads the resolved layout, so call it from `bind`, after the reference has been laid out.

## Authoring UI in UXML

UI markup lives in your mod's [Unity project](/unity-project) under `unity/Assets/UI/`, authored as UXML. Compiling the mod builds it into a bundle, the same pipeline that ships [prefabs](/assets/additions/prefabs).

UXML and USS are Unity's own UI Toolkit markup and stylesheet formats, not Jiangyu's, so any UI Toolkit reference applies and you can author them in the Unity UI Builder. See Unity's [UXML](https://docs.unity3d.com/Manual/UIE-UXML.html) and [USS](https://docs.unity3d.com/Manual/UIE-USS.html) manuals for the full element and property reference. The game's own screens are built in UI Toolkit, which is what lets your markup sit inside them.

The bar from the [worked example](#a-worked-example), `unity/Assets/UI/relationship_bar.uxml`:

```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <Style src="relationship_bar.uss" />
    <ui:VisualElement name="RelationshipBar" class="stat-bar">
        <ui:VisualElement name="Fill" class="stat-bar__fill" />
        <ui:Label name="Value" class="stat-bar__value" text="0" />
    </ui:VisualElement>
</ui:UXML>
```

The element names (`Fill`, `Value`) are the handles your `bind` code reaches with `element.Q(...)`, and the classes are styled in `unity/Assets/UI/relationship_bar.uss`:

```css
.stat-bar {
    flex-direction: row;
    align-items: center;
    height: 6px;
    background-color: rgba(0, 0, 0, 0.4);
}

.stat-bar__fill {
    height: 100%;
    background-color: rgb(120, 170, 220);
}

.stat-bar__value {
    margin-left: 4px;
    font-size: 10px;
    -unity-text-align: middle-left;
}
```

The stylesheet sets the static look. The `bind` callback drives the parts that change, the fill width and the value text. Note that USS keeps Unity's `-unity-` prefixes for engine-specific properties.

At runtime the string form of `Inject` loads the UXML from your mod's bundle by name. The name is the file's path under `unity/Assets/UI/` with the extension dropped, and [subfolders work](/unity-project#naming-and-subfolders) the same as for prefabs:

```csharp
UI.Inject(target, "relationship_bar");            // unity/Assets/UI/relationship_bar.uxml
UI.Inject(target, "strategy/relationship_bar");   // unity/Assets/UI/strategy/relationship_bar.uxml
```

To hold the asset yourself, load it through your mod's [assets](/sdk/#assets) and pass it in:

```csharp
var asset = Context.Assets.Load<VisualTreeAsset>("strategy/relationship_bar");
UI.Inject(target, asset);
```

## Keeping an injection current

`UI.Inject` hands back a `UiInjection`:

| Method | Effect |
| --- | --- |
| `injection.Refresh()` | rebuild the element against the current tree and data |
| `injection.Remove()` | take the element out and stop maintaining the injection |

You never re-inject on a screen change yourself. The loader watches the active screen and re-applies your injections when it rebuilds, and an injection is idempotent, so it never doubles up. Set it up once in `OnInit`:

```csharp
public override void OnInit()
{
    UI.Inject(
        UiTarget.Screen<ArmoryUIScreen>().After(UiSelector.Name("HealthBar")),
        "relationship_bar");
}
```

Call `Refresh()` when the data behind your element changes, a value the bar shows went up, and `Remove()` to take it away for good.

## Modals and overlays

`UiTarget.Overlay()` injects a full-screen layer above the active screen, the basis for a modal. The element is stretched to cover the screen, so a UXML root styled `position: absolute` fills it and dims what is behind. Inject it once, capture the element in `bind`, and open and close it by toggling its visibility rather than re-injecting:

```csharp
VisualElement modal = null;

UI.Inject(UiTarget.Overlay(), "gift_modal", element =>
{
    modal = element;
    element.style.display = DisplayStyle.None;                 // start closed
    element.Q<Button>("Close").clicked +=
        () => element.style.display = DisplayStyle.None;
});

void OpenGiftModal() => modal.style.display = DisplayStyle.Flex;
```

## A worked example

A self-contained behaviour mod that adds a relationship bar under the health bar of every unit slot on the armoury screen, and refreshes it when the value changes.

```csharp
using Jiangyu.Sdk;
using Jiangyu.Game;
using UnityEngine.UIElements;
using Il2CppMenace.UI.Strategy;

public sealed class RelationshipBarSystem : JiangyuSystem
{
    private UiInjection _bar;

    public override void OnInit()
    {
        _bar = UI.InjectEach(
            UiTarget.ActiveScreen().Each(UiSelector.TypeName("ArmoryUnitSelectSlot")),
            "relationship_bar",
            (bar, slot) =>
            {
                var anchor = UI.Find(slot, UiSelector.Name("HealthBar"));
                if (anchor != null)
                    bar.StackAfter(anchor, gap: 2f);
                bar.Q<VisualElement>("Fill").style.width = Length.Percent(50);
            });
    }

    // Re-run the bind against the current data after a gift lands.
    public void OnRelationshipChanged() => _bar.Refresh();
}
```

The markup and its look live in `Assets/UI/relationship_bar.uxml` and `relationship_bar.uss`. The mod targets the screen, scopes to each slot, anchors under the health bar, and the loader keeps the bar in place as the player switches units.
