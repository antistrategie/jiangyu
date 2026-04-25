using System.Globalization;
using Jiangyu.Core.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class KdlEditorRoundTripTests
{
    [Fact]
    public void SimplePatch_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "player_squad.darby" {
                set "HudYOffsetScale" 5.0
                set "IsActive" #true
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);

        var node = doc.Nodes[0];
        Assert.Equal(KdlEditorNodeKind.Patch, node.Kind);
        Assert.Equal("EntityTemplate", node.TemplateType);
        Assert.Equal("player_squad.darby", node.TemplateId);
        Assert.Equal(2, node.Directives.Count);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Single(doc2.Nodes);
        Assert.Equal(2, doc2.Nodes[0].Directives.Count);
    }

    [Fact]
    public void RefValue_RoundTrips()
    {
        const string kdl = """
            patch "UnitLeaderTemplate" "squad_leader.darby" {
                append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.greifinger"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Append, d.Op);
        Assert.Equal(KdlEditorValueKind.TemplateReference, d.Value!.Kind);
        Assert.Equal("PerkTreeTemplate", d.Value.ReferenceType);
        Assert.Equal("perk_tree.greifinger", d.Value.ReferenceId);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("ref=\"PerkTreeTemplate\"", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal("perk_tree.greifinger", doc2.Nodes[0].Directives[0].Value!.ReferenceId);
    }

    [Fact]
    public void EnumValue_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {
                set "Faction" enum="FactionType" "Rebel"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorValueKind.Enum, d.Value!.Kind);
        Assert.Equal("FactionType", d.Value.EnumType);
        Assert.Equal("Rebel", d.Value.EnumValue);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("Rebel", doc2.Nodes[0].Directives[0].Value!.EnumValue);
    }

    [Fact]
    public void InsertWithIndex_RoundTrips()
    {
        const string kdl = """
            patch "UnitLeaderTemplate" "squad_leader.darby" {
                insert "PerkTrees" index=0 ref="PerkTreeTemplate" "perk_tree.tech"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Insert, d.Op);
        Assert.Equal(0, d.Index);
        Assert.Equal(KdlEditorValueKind.TemplateReference, d.Value!.Kind);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("index=0", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal(0, doc2.Nodes[0].Directives[0].Index);
    }

    [Fact]
    public void RemoveIndexed_RoundTrips()
    {
        const string kdl = """
            patch "UnitLeaderTemplate" "squad_leader.darby" {
                remove "PerkTrees" index=0
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Remove, d.Op);
        Assert.Equal("PerkTrees", d.FieldPath);
        Assert.Equal(0, d.Index);
        Assert.Null(d.Value);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("remove \"PerkTrees\" index=0", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("PerkTrees", doc2.Nodes[0].Directives[0].FieldPath);
        Assert.Equal(0, doc2.Nodes[0].Directives[0].Index);
    }

    [Fact]
    public void CompositeValue_RoundTrips()
    {
        const string kdl = """
            patch "PerkTreeTemplate" "perk_tree.darby" {
                append "Perks" composite="Perk" {
                    set "Skill" ref="PerkTemplate" "perk.athletic"
                    set "Tier" 3
                }
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        var d = doc.Nodes[0].Directives[0];
        Assert.Equal(KdlEditorOp.Append, d.Op);
        Assert.Equal(KdlEditorValueKind.Composite, d.Value!.Kind);
        Assert.Equal("Perk", d.Value.CompositeType);
        Assert.NotNull(d.Value.CompositeFields);
        Assert.Equal(2, d.Value.CompositeFields.Count);
        Assert.Equal(KdlEditorValueKind.TemplateReference, d.Value.CompositeFields["Skill"].Kind);
        Assert.Equal(KdlEditorValueKind.Int32, d.Value.CompositeFields["Tier"].Kind);
        Assert.Equal(3, d.Value.CompositeFields["Tier"].Int32);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("composite=\"Perk\"", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("Perk", doc2.Nodes[0].Directives[0].Value!.CompositeType);
        Assert.Equal(2, doc2.Nodes[0].Directives[0].Value!.CompositeFields!.Count);
    }

    [Fact]
    public void CloneWithInlineDirectives_RoundTrips()
    {
        const string kdl = """
            clone "UnitLeaderTemplate" from="leader.alice" id="leader.alice_clone" {
                set "DisplayName" "Alice Mk.II"
                set "InitialAttributes.Agility" 100
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);

        var node = doc.Nodes[0];
        Assert.Equal(KdlEditorNodeKind.Clone, node.Kind);
        Assert.Equal("UnitLeaderTemplate", node.TemplateType);
        Assert.Equal("leader.alice", node.SourceId);
        Assert.Equal("leader.alice_clone", node.CloneId);
        Assert.Equal(2, node.Directives.Count);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("from=\"leader.alice\"", text);
        Assert.Contains("id=\"leader.alice_clone\"", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal(KdlEditorNodeKind.Clone, doc2.Nodes[0].Kind);
        Assert.Equal(2, doc2.Nodes[0].Directives.Count);
    }

    [Fact]
    public void MultipleNodes_PreservesOrder()
    {
        const string kdl = """
            patch "EntityTemplate" "a" {
                set "X" 1
            }

            clone "UnitLeaderTemplate" from="b" id="c" {
                set "Y" 2
            }

            patch "EntityTemplate" "d" {
                set "Z" 3
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(3, doc.Nodes.Count);
        Assert.Equal(KdlEditorNodeKind.Patch, doc.Nodes[0].Kind);
        Assert.Equal(KdlEditorNodeKind.Clone, doc.Nodes[1].Kind);
        Assert.Equal(KdlEditorNodeKind.Patch, doc.Nodes[2].Kind);
        Assert.Equal("a", doc.Nodes[0].TemplateId);
        Assert.Equal("d", doc.Nodes[2].TemplateId);
    }

    [Fact]
    public void ParseErrors_ReportedNotCrash()
    {
        const string kdl = "this is not valid KDL {{{}";

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.NotEmpty(doc.Errors);
    }

    [Fact]
    public void UnknownTopLevelNode_ReportsError()
    {
        const string kdl = """
            foobar "something" {
                set "x" 1
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.NotEmpty(doc.Errors);
        Assert.Contains(doc.Errors, e => e.Message.Contains("foobar"));
    }

    [Fact]
    public void BooleanValues_RoundTrip()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {
                set "Active" #true
                set "Hidden" #false
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(true, doc.Nodes[0].Directives[0].Value!.Boolean);
        Assert.Equal(false, doc.Nodes[0].Directives[1].Value!.Boolean);

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("#true", text);
        Assert.Contains("#false", text);
    }

    [Fact]
    public void StringValue_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {
                set "Name" "Hello World"
            }
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(KdlEditorValueKind.String, doc.Nodes[0].Directives[0].Value!.Kind);
        Assert.Equal("Hello World", doc.Nodes[0].Directives[0].Value!.String);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal("Hello World", doc2.Nodes[0].Directives[0].Value!.String);
    }

    [Fact]
    public void EmptyPatch_Accepted()
    {
        const string kdl = """
            patch "EntityTemplate" "test" {}
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);
        Assert.Empty(doc.Nodes[0].Directives);
    }

    [Fact]
    public void CloneWithoutDirectives_RoundTrips()
    {
        const string kdl = """
            clone "UnitLeaderTemplate" from="base" id="derived"
            """;

        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Single(doc.Nodes);
        Assert.Equal(KdlEditorNodeKind.Clone, doc.Nodes[0].Kind);
        Assert.Empty(doc.Nodes[0].Directives);

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Equal(KdlEditorNodeKind.Clone, doc2.Nodes[0].Kind);
        Assert.Equal("base", doc2.Nodes[0].SourceId);
        Assert.Equal("derived", doc2.Nodes[0].CloneId);
    }

    [Fact]
    public void ParseText_RecoversSyntaxErrorsAcrossBlocks()
    {
        var input = """
            clone "UnitLeaderTemplate" from="squad_leader.darby" id="squad_leader.darby_jiangyu_clone" {
                set "InitialAttributes.Vitality" 77
            }a

            patch "UnitLeaderTemplate" "squad_leader.darby" {
                set "InitialAttributes.Agility" 100
                append "PerkTrees" ref="PerkTreeTemplate" "perk_tree.greifinger"
                insert "PerkTrees" index=0 ref="PerkTreeTemplate" "perk_tree.tech"
                remove "PerkTrees[0]"
            }adwad ggggg
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Equal(2, doc.Errors.Count);
        Assert.Equal(3, doc.Errors[0].Line);  // }a
        Assert.Equal(10, doc.Errors[1].Line); // }adwad ggggg
    }

    [Fact]
    public void ParseText_RecoveryStillParsesValidBlocks()
    {
        // First block has a syntax error, second is valid
        var input = """
            clone "UnitLeaderTemplate" from="base" id="derived" {
                set "InitialAttributes.Vitality" 77
            }JUNK

            patch "UnitLeaderTemplate" "squad_leader.darby" {
                set "InitialAttributes.Agility" 100
            }
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Single(doc.Errors);
        Assert.Equal(3, doc.Errors[0].Line);
        Assert.Single(doc.Nodes);
        Assert.Equal(KdlEditorNodeKind.Patch, doc.Nodes[0].Kind);
    }

    [Fact]
    public void ParseText_ValidInputBypassesRecovery()
    {
        // No syntax errors — should take the fast path, not recovery
        var input = """
            patch "EntityTemplate" "a" {
                set "X" 1
            }

            clone "UnitLeaderTemplate" from="b" id="c" {
                set "Y" 2
            }
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Empty(doc.Errors);
        Assert.Equal(2, doc.Nodes.Count);
    }

    [Fact]
    public void ParseText_RecoveryWithSemanticAndSyntaxErrors()
    {
        // Block 1: valid KDL but unknown node (semantic error)
        // Block 2: syntax error
        var input = """
            foobar "something" {
                set "x" 1
            }

            patch "EntityTemplate" "test" {
                set "Y" 2
            }BROKEN
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Equal(2, doc.Errors.Count);
        Assert.Contains(doc.Errors, e => e.Message.Contains("foobar"));
        Assert.Contains(doc.Errors, e => e.Line == 7);
    }

    [Fact]
    public void ParseText_RecoveryWithBracesInStrings()
    {
        // Braces inside strings should not confuse the block splitter
        var input = """
            patch "EntityTemplate" "test" {
                set "Name" "hello { world }"
            }OOPS
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.Single(doc.Errors);
        Assert.Equal(3, doc.Errors[0].Line);
    }

    [Fact]
    public void ParseText_RecoveryWithUnclosedBlock()
    {
        // Unclosed brace should still be captured as a block
        var input = """
            patch "EntityTemplate" "test" {
                set "X" 1
            """;
        var doc = KdlTemplateParser.ParseText(input);
        Assert.NotEmpty(doc.Errors);
    }

    [Fact]
    public void Serialise_FloatValue_UsesInvariantCulture()
    {
        // Switch the current culture to one that uses ',' as the decimal
        // separator. The serialiser must still emit '.' so KDL can re-parse it.
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var doc = new KdlEditorDocument
            {
                Nodes =
                [
                    new KdlEditorNode
                    {
                        Kind = KdlEditorNodeKind.Patch,
                        TemplateType = "EntityTemplate",
                        TemplateId = "x",
                        Directives =
                        [
                            new KdlEditorDirective
                            {
                                Op = KdlEditorOp.Set,
                                FieldPath = "Scale",
                                Value = new KdlEditorValue
                                {
                                    Kind = KdlEditorValueKind.Single,
                                    Single = 1.5f,
                                },
                            },
                        ],
                    },
                ],
            };

            var text = KdlTemplateSerialiser.Serialise(doc);
            Assert.Contains("1.5", text);
            Assert.DoesNotContain("1,5", text);

            // Round-trip through the parser to confirm KDL can read it back.
            var doc2 = KdlTemplateParser.ParseText(text);
            Assert.Empty(doc2.Errors);
            Assert.Equal(1.5f, doc2.Nodes[0].Directives[0].Value!.Single);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Serialise_IntegerValue_UsesInvariantDigits()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            // ar-SA traditionally renders Arabic-Indic digits when the
            // NumberFormat uses them; force a culture and verify the
            // serialiser still emits ASCII digits so KDL can parse them.
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");

            var doc = new KdlEditorDocument
            {
                Nodes =
                [
                    new KdlEditorNode
                    {
                        Kind = KdlEditorNodeKind.Patch,
                        TemplateType = "EntityTemplate",
                        TemplateId = "x",
                        Directives =
                        [
                            new KdlEditorDirective
                            {
                                Op = KdlEditorOp.Insert,
                                FieldPath = "Skills",
                                Index = 12,
                                Value = new KdlEditorValue
                                {
                                    Kind = KdlEditorValueKind.Int32,
                                    Int32 = 42,
                                },
                            },
                        ],
                    },
                ],
            };

            var text = KdlTemplateSerialiser.Serialise(doc);
            Assert.Contains("index=12", text);
            Assert.Contains("42", text);

            var doc2 = KdlTemplateParser.ParseText(text);
            Assert.Empty(doc2.Errors);
            Assert.Equal(12, doc2.Nodes[0].Directives[0].Index);
            Assert.Equal(42, doc2.Nodes[0].Directives[0].Value!.Int32);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    public void Serialise_StringValue_EscapesQuotesAndBackslashes()
    {
        var doc = new KdlEditorDocument
        {
            Nodes =
            [
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "EntityTemplate",
                    TemplateId = "x",
                    Directives =
                    [
                        new KdlEditorDirective
                        {
                            Op = KdlEditorOp.Set,
                            FieldPath = "Name",
                            Value = new KdlEditorValue
                            {
                                Kind = KdlEditorValueKind.String,
                                String = "He said \"hi\" \\path",
                            },
                        },
                    ],
                },
            ],
        };

        var text = KdlTemplateSerialiser.Serialise(doc);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal("He said \"hi\" \\path", doc2.Nodes[0].Directives[0].Value!.String);
    }

    [Fact]
    public void Serialise_EmptyDocument_ReturnsEmptyString()
    {
        var text = KdlTemplateSerialiser.Serialise(new KdlEditorDocument());
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Serialise_PatchWithNoDirectives_EmitsEmptyBlock()
    {
        // Empty patches re-parse as errors (a patch must have at least one
        // operation). The serialiser still has to emit something stable so the
        // editor can show what it has, even if the result is invalid until the
        // user fills it in.
        var doc = new KdlEditorDocument
        {
            Nodes =
            [
                new KdlEditorNode
                {
                    Kind = KdlEditorNodeKind.Patch,
                    TemplateType = "EntityTemplate",
                    TemplateId = "x",
                    Directives = [],
                },
            ],
        };

        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("patch \"EntityTemplate\" \"x\" {}", text);
    }

    [Fact]
    public void ZeroFloat_RoundTrips()
    {
        const string kdl = """
            patch "EntityTemplate" "x" {
                set "Scale" 0.0
            }
            """;
        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);
        Assert.Equal(0f, doc.Nodes[0].Directives[0].Value!.Single);

        var text = KdlTemplateSerialiser.Serialise(doc);
        // Must still contain a decimal point so the parser sees a float, not an int.
        Assert.Contains("0.0", text);
        var doc2 = KdlTemplateParser.ParseText(text);
        Assert.Empty(doc2.Errors);
        Assert.Equal(KdlEditorValueKind.Single, doc2.Nodes[0].Directives[0].Value!.Kind);
    }

    [Fact]
    public void EmptyComposite_RoundTrips()
    {
        const string kdl = """
            patch "PerkTreeTemplate" "x" {
                append "Perks" composite="Perk" {
                    set "Tier" 1
                }
            }
            """;
        var doc = KdlTemplateParser.ParseText(kdl);
        Assert.Empty(doc.Errors);

        // Strip the composite's fields and re-serialise — the serialiser should
        // still emit a valid empty composite block.
        doc.Nodes[0].Directives[0].Value!.CompositeFields = null;
        var text = KdlTemplateSerialiser.Serialise(doc);
        Assert.Contains("composite=\"Perk\"", text);
    }
}
