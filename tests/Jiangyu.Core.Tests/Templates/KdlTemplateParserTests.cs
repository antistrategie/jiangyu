using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class KdlTemplateParserTests
{
    private readonly TestLogSink _log = new();

    [Fact]
    public void ParseAll_SimplePatch_ProducesCorrectModel()
    {
        var dir = SetupKdl("test.kdl", """
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Patches);
        Assert.Empty(result.Clones);

        var patch = result.Patches[0];
        Assert.Equal("EntityTemplate", patch.TemplateType);
        Assert.Equal("player_squad.darby", patch.TemplateId);
        Assert.Single(patch.Set);

        var op = patch.Set[0];
        Assert.Equal(CompiledTemplateOp.Set, op.Op);
        Assert.Equal("HudYOffsetScale", op.FieldPath);
        Assert.Equal(CompiledTemplateValueKind.Single, op.Value!.Kind);
        Assert.Equal(5.0f, op.Value.Single);
    }

    [Fact]
    public void ParseAll_Clone_WithInlinePatches()
    {
        var dir = SetupKdl("clones.kdl", """
            clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_custom" {
                set "InitialAttributes.Vitality" 80
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Clones);
        Assert.Single(result.Patches);

        var clone = result.Clones[0];
        Assert.Equal("UnitLeaderTemplate", clone.TemplateType);
        Assert.Equal("squad_leader.darby", clone.SourceId);
        Assert.Equal("squad_leader.darby_custom", clone.CloneId);

        // Inline patches target the clone ID
        var patch = result.Patches[0];
        Assert.Equal("UnitLeaderTemplate", patch.TemplateType);
        Assert.Equal("squad_leader.darby_custom", patch.TemplateId);
        Assert.Single(patch.Set);
        Assert.Equal("InitialAttributes.Vitality", patch.Set[0].FieldPath);
    }

    [Fact]
    public void ParseAll_CloneWithoutBody_ProducesNoPatches()
    {
        var dir = SetupKdl("clone-only.kdl", """
            clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_copy"
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Clones);
        Assert.Empty(result.Patches);
    }

    [Fact]
    public void ParseAll_AllValueKinds()
    {
        var dir = SetupKdl("values.kdl", """
            patch "SkillTemplate" "active.aimed_shot" {
                set "FloatField" 3.14
                set "BoolField" #true
                set "IntField" 42
                set "StringField" "hello"
                set "RefField" ref="PerkTreeTemplate" "perk_tree.greifinger"
                set "EnumField" enum="MovementType" "Sprint"
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var ops = result.Patches[0].Set;
        Assert.Equal(6, ops.Count);

        // Float
        var floatValue = Assert.IsType<CompiledTemplateValue>(ops[0].Value);
        Assert.Equal(CompiledTemplateValueKind.Single, floatValue.Kind);
        Assert.Equal(3.14f, floatValue.Single);

        // Boolean
        var boolValue = Assert.IsType<CompiledTemplateValue>(ops[1].Value);
        Assert.Equal(CompiledTemplateValueKind.Boolean, boolValue.Kind);
        Assert.True(boolValue.Boolean == true);

        // Integer -> Int32
        var intValue = Assert.IsType<CompiledTemplateValue>(ops[2].Value);
        Assert.Equal(CompiledTemplateValueKind.Int32, intValue.Kind);
        Assert.Equal(42, intValue.Int32);

        // String
        var stringValue = Assert.IsType<CompiledTemplateValue>(ops[3].Value);
        Assert.Equal(CompiledTemplateValueKind.String, stringValue.Kind);
        Assert.Equal("hello", stringValue.String);

        // TemplateReference
        var referenceValue = Assert.IsType<CompiledTemplateValue>(ops[4].Value);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, referenceValue.Kind);
        var reference = Assert.IsType<CompiledTemplateReference>(referenceValue.Reference);
        Assert.Equal("PerkTreeTemplate", reference.TemplateType);
        Assert.Equal("perk_tree.greifinger", reference.TemplateId);

        // Enum
        var enumValue = Assert.IsType<CompiledTemplateValue>(ops[5].Value);
        Assert.Equal(CompiledTemplateValueKind.Enum, enumValue.Kind);
        Assert.Equal("MovementType", enumValue.EnumType);
        Assert.Equal("Sprint", enumValue.EnumValue);
    }

    [Fact]
    public void ParseAll_CollectionOps()
    {
        var dir = SetupKdl("collections.kdl", """
            patch "WeaponTemplate" "weapon.test" {
                append "SkillsGranted" ref="SkillTemplate" "active.aimed_shot"
                insert "PerkTrees" index=0 ref="PerkTreeTemplate" "perk_tree.tech"
                remove "PerkTrees" index=2
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var ops = result.Patches[0].Set;
        Assert.Equal(3, ops.Count);

        // Append
        Assert.Equal(CompiledTemplateOp.Append, ops[0].Op);
        Assert.Equal("SkillsGranted", ops[0].FieldPath);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, ops[0].Value!.Kind);

        // InsertAt
        Assert.Equal(CompiledTemplateOp.InsertAt, ops[1].Op);
        Assert.Equal("PerkTrees", ops[1].FieldPath);
        Assert.Equal(0, ops[1].Index);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, ops[1].Value!.Kind);

        // Remove
        Assert.Equal(CompiledTemplateOp.Remove, ops[2].Op);
        Assert.Equal("PerkTrees", ops[2].FieldPath);
        Assert.Equal(2, ops[2].Index);
        Assert.Null(ops[2].Value);
    }

    [Fact]
    public void ParseAll_Composite()
    {
        var dir = SetupKdl("composite.kdl", """
            patch "PerkTreeTemplate" "perk_tree.darby" {
                append "Perks" composite="Perk" {
                    set "Skill" ref="PerkTemplate" "perk.athletic"
                    set "Tier" 3
                }
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal(CompiledTemplateOp.Append, op.Op);
        Assert.Equal(CompiledTemplateValueKind.Composite, op.Value!.Kind);

        var composite = op.Value.Composite!;
        Assert.Equal("Perk", composite.TypeName);
        Assert.Equal(2, composite.Fields.Count);
        Assert.Equal(CompiledTemplateValueKind.TemplateReference, composite.Fields["Skill"].Kind);
        Assert.Equal(CompiledTemplateValueKind.Int32, composite.Fields["Tier"].Kind);
        Assert.Equal(3, composite.Fields["Tier"].Int32);
    }

    [Fact]
    public void ParseAll_DuplicateInFile_IsError()
    {
        var dir = SetupKdl("dup.kdl", """
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
            }
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 10.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
        Assert.Contains("duplicate", _log.Errors.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_DuplicateAcrossFiles_IsError()
    {
        var dir = SetupKdl(
            ("a.kdl", """
                patch "EntityTemplate" "player_squad.darby" {
                    set "HudYOffsetScale" 5.0
                }
                """),
            ("b.kdl", """
                patch "EntityTemplate" "player_squad.darby" {
                    set "HudYOffsetScale" 10.0
                }
                """));

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
        Assert.Contains("duplicate", _log.Errors.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_RecursiveDiscovery()
    {
        var dir = SetupKdl(
            ("units/darby.kdl", """
                patch "EntityTemplate" "player_squad.darby" {
                    set "HudYOffsetScale" 5.0
                }
                """),
            ("weapons/carbine.kdl", """
                patch "WeaponTemplate" "weapon.generic_carbine" {
                    set "Damage" 25
                }
                """));

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(2, result.Patches.Count);
    }

    [Fact]
    public void ParseAll_UnknownTopLevelNode_IsError()
    {
        var dir = SetupKdl("bad.kdl", """
            modify "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
        Assert.Contains("unknown top-level node", _log.Errors.First(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAll_MissingArguments_IsError()
    {
        var dir = SetupKdl("bad.kdl", """
            patch "EntityTemplate" {
                set "HudYOffsetScale" 5.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.True(result.ErrorCount > 0);
    }

    [Fact]
    public void ParseAll_EmptyPatchBody_IsOk()
    {
        var dir = SetupKdl("empty.kdl", """
            patch "EntityTemplate" "player_squad.darby"
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Single(result.Patches);
        Assert.Empty(result.Patches[0].Set);
    }

    [Fact]
    public void ParseAll_NegativeInteger_EmitsInt32()
    {
        var dir = SetupKdl("neg.kdl", """
            patch "EntityTemplate" "player_squad.darby" {
                set "SomeField" -5
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        var op = result.Patches[0].Set[0];
        Assert.Equal(CompiledTemplateValueKind.Int32, op.Value!.Kind);
        Assert.Equal(-5, op.Value.Int32);
    }

    [Fact]
    public void ParseAll_DottedFieldPath_Preserved()
    {
        var dir = SetupKdl("dotted.kdl", """
            patch "SkillTemplate" "active.aimed_shot" {
                set "ProjectileData.TimeScale" 99.0
            }
            """);

        var result = KdlTemplateParser.ParseAll(dir, _log);

        Assert.Equal(0, result.ErrorCount);
        Assert.Equal("ProjectileData.TimeScale", result.Patches[0].Set[0].FieldPath);
    }

    // --- Helpers ---

    private static string SetupKdl(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jiangyu-test-kdl-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var subDir = Path.GetDirectoryName(Path.Combine(dir, fileName));
        if (subDir != null)
            Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    private static string SetupKdl(params (string fileName, string content)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jiangyu-test-kdl-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var (fileName, content) in files)
        {
            var fullPath = Path.Combine(dir, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        return dir;
    }

    private sealed class TestLogSink : Jiangyu.Core.Abstractions.ILogSink
    {
        public List<string> Errors { get; } = [];
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }
        public void Warning(string message) => Warnings.Add(message);
        public void Error(string message) => Errors.Add(message);
    }
}
