using Jiangyu.Codegen.Docs;
using Jiangyu.Sdk;
using Xunit;

namespace Jiangyu.Codegen.Tests;

public class DocsEmitTests
{
    [Fact]
    public void EmitVerbs_groups_by_layer_then_class_and_renders_rows()
    {
        var md = DocsEmit.EmitVerbs(new[]
        {
            new VerbDoc("Tactical", "Units", "Spawn", "Spawn(EntityTemplate, FactionType, Tile)", "Spawn a unit."),
            new VerbDoc("Tactical", "Combat", "CanSee", "CanSee(Tile, Tile)", "Line of sight."),
            new VerbDoc("Strategy", "Campaign", "Active", "Active", "Whether a campaign is loaded."),
        });
        Assert.Contains("# Verb reference", md);
        Assert.Contains("## Strategy", md);
        Assert.Contains("## Tactical", md);
        Assert.Contains("### Combat", md);
        Assert.Contains("### Units", md);
        Assert.Contains("| `Units.Spawn(EntityTemplate, FactionType, Tile)` | Spawn a unit. |", md);
        // Combat sorts before Units within the Tactical layer.
        Assert.True(md.IndexOf("### Combat", System.StringComparison.Ordinal) < md.IndexOf("### Units", System.StringComparison.Ordinal));
    }

    [Fact]
    public void EmitVerbs_escapes_pipes_in_a_summary()
    {
        var md = DocsEmit.EmitVerbs(new[]
        {
            new VerbDoc("Tactical", "Combat", "Damage", "Damage(Actor, int)", "Deal damage | through the pipeline."),
        });
        Assert.Contains("Deal damage \\| through the pipeline.", md);
    }

    [Fact]
    public void EmitHooks_renders_payload_table_and_source_line()
    {
        var hooks = new[]
        {
            new HookDescriptor("Tactical", "RoundStarted", typeof(RoundStartedContext), HookKind.Event,
                "TacticalManager.OnRoundStart", "A new tactical round began.",
                new[] { new HookPayloadField("Round", "int", "The round number.") }),
        };
        var md = DocsEmit.EmitHooks(hooks);
        Assert.Contains("# Hook reference", md);
        Assert.Contains("### RoundStarted", md);
        Assert.Contains("- **Context:** `RoundStartedContext`", md);
        Assert.Contains("- **Source:** event (`TacticalManager.OnRoundStart`)", md);
        Assert.Contains("| `Round` | `int` | The round number. |", md);
    }

    [Fact]
    public void EmitHooks_marks_a_synthetic_hook_as_raised_by_jiangyu()
    {
        var hooks = new[]
        {
            new HookDescriptor("Tactical", "MissionStarted", typeof(MissionStartedContext), HookKind.Synthetic,
                "", "A tactical mission loaded.", System.Array.Empty<HookPayloadField>()),
        };
        var md = DocsEmit.EmitHooks(hooks);
        Assert.Contains("- **Source:** synthetic (raised by Jiangyu)", md);
    }

    [Fact]
    public void EmitUi_renders_type_summary_as_section_intro_then_member_rows()
    {
        var md = DocsEmit.EmitUi(new[]
        {
            new UiClassDoc("Components", "TextButton", "A native-looking text button.", new[]
            {
                new UiMemberDoc("TextButton(string, bool)", "Build the button."),
                new UiMemberDoc("OnClick(Action)", "Run a handler on click."),
            }),
        });
        Assert.Contains("# UI reference", md);
        Assert.Contains("## Components", md);
        Assert.Contains("### TextButton", md);
        // The type summary is prose under the heading, not a table row.
        Assert.Contains("A native-looking text button.", md);
        Assert.Contains("| `TextButton(string, bool)` | Build the button. |", md);
        Assert.Contains("| `OnClick(Action)` | Run a handler on click. |", md);
    }

    [Fact]
    public void EmitUi_escapes_angle_brackets_in_prose_but_leaves_signatures_in_code()
    {
        var md = DocsEmit.EmitUi(new[]
        {
            new UiClassDoc("Injection and helpers", "UI", "Link the USS with a <Style> tag.", new[]
            {
                new UiMemberDoc("Type<T>", "Match an element whose type is T, as with Type<T>."),
            }),
        });
        // VitePress compiles markdown through the Vue SFC parser, so a generic or literal
        // tag in prose must be HTML-escaped or it reads as an unclosed element.
        Assert.Contains("a &lt;Style&gt; tag", md);
        Assert.Contains("as with Type&lt;T&gt;.", md);
        // The signature stays raw inside its backtick span (markdown escapes inline code).
        Assert.Contains("| `Type<T>` |", md);
    }

    [Fact]
    public void EmitUi_orders_groups_helpers_then_components_then_audio()
    {
        var md = DocsEmit.EmitUi(new[]
        {
            new UiClassDoc("Audio", "Sound", "UI sounds.", System.Array.Empty<UiMemberDoc>()),
            new UiClassDoc("Components", "Flyout", "A window-framed panel.", System.Array.Empty<UiMemberDoc>()),
            new UiClassDoc("Injection and helpers", "UI", "Adds mod UI into the game's screens.", System.Array.Empty<UiMemberDoc>()),
        });
        var helpers = md.IndexOf("## Injection and helpers", System.StringComparison.Ordinal);
        var components = md.IndexOf("## Components", System.StringComparison.Ordinal);
        var audio = md.IndexOf("## Audio", System.StringComparison.Ordinal);
        Assert.True(helpers < components && components < audio);
    }
}
