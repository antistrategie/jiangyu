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
}
