using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

/// <summary>
/// Drives <see cref="TemplateOperationWalker"/> against a small in-memory
/// adapter (<see cref="FakeNode"/>) so the descent + inner walk + op
/// dispatch is exercised without booting either of the real appliers. The
/// fake records every call so the test can assert which primitive the
/// walker reached and with what arguments.
/// </summary>
public sealed class TemplateOperationWalkerTests
{
    [Fact]
    public void Execute_SetScalar_DispatchesToTrySetScalar()
    {
        FakeNode root = FakeNode.Object("root", ("Speed", FakeNode.Scalar("Speed")));
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Speed",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(7)),
            out string? error);

        Assert.Equal(OperationResult.Applied, result);
        Assert.Null(error);
        Assert.Equal(new[] { "SetScalar:Speed=7" }, visitor.Calls);
    }

    [Fact]
    public void Execute_SetElement_TerminalBracketResolvesToSetElement()
    {
        FakeNode root = FakeNode.Object("root", ("Skills", FakeNode.Scalar("Skills")));
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Skills[3]",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(11)),
            out string? error);

        Assert.Equal(OperationResult.Applied, result);
        Assert.Null(error);
        Assert.Equal(new[] { "SetElement:Skills[3]=11" }, visitor.Calls);
    }

    [Fact]
    public void Execute_SetElement_OpIndexResolvesToSetElement()
    {
        FakeNode root = FakeNode.Object("root", ("Skills", FakeNode.Scalar("Skills")));
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Skills",
                index: 2,
                indexPath: null,
                descent: null,
                value: Int32(99)),
            out _);

        Assert.Equal(OperationResult.Applied, result);
        Assert.Equal(new[] { "SetElement:Skills[2]=99" }, visitor.Calls);
    }

    [Fact]
    public void Execute_SetCell_DispatchesToTrySetCell()
    {
        FakeNode root = FakeNode.Object("root", ("Grid", FakeNode.Scalar("Grid")));
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Grid",
                index: null,
                indexPath: new[] { 1, 4 },
                descent: null,
                value: Int32(5)),
            out _);

        Assert.Equal(OperationResult.Applied, result);
        Assert.Equal(new[] { "SetCell:Grid[1,4]=5" }, visitor.Calls);
    }

    [Fact]
    public void Execute_SetCell_RejectsScalarIndexInCombination()
    {
        FakeNode root = FakeNode.Object("root", ("Grid", FakeNode.Scalar("Grid")));
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Grid",
                index: 2,
                indexPath: new[] { 1, 4 },
                descent: null,
                value: Int32(5)),
            out string? error);

        Assert.Equal(OperationResult.ConversionFailed, result);
        Assert.Contains("cell address", error);
        Assert.Empty(visitor.Calls);
    }

    [Fact]
    public void Execute_NotSupported_FromVisitorPropagates()
    {
        FakeNode root = FakeNode.Object("root", ("Grid", FakeNode.Scalar("Grid")));
        FakeVisitor visitor = new(root) { SetCellResult = OperationResult.NotSupported };

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Grid",
                index: null,
                indexPath: new[] { 0, 0 },
                descent: null,
                value: Int32(0)),
            out _);

        Assert.Equal(OperationResult.NotSupported, result);
        Assert.Equal(OperationResult.NotSupported, visitor.LastCompleted);
    }

    [Fact]
    public void Execute_Append_DispatchesToTryAppend()
    {
        FakeNode root = FakeNode.Object("root", ("Names", FakeNode.Scalar("Names")));
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Append,
                fieldPath: "Names",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(1)),
            out _);

        Assert.Equal(OperationResult.Applied, result);
        Assert.Equal(new[] { "Append:Names=1" }, visitor.Calls);
    }

    [Fact]
    public void Execute_InsertAt_RequiresIndex()
    {
        FakeVisitor visitor = new(FakeNode.Object("root"));

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            visitor.Root,
            new TemplateOperationView(
                CompiledTemplateOp.InsertAt,
                fieldPath: "Names",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(0)),
            out string? error);

        Assert.Equal(OperationResult.ConversionFailed, result);
        Assert.Contains("no index", error);
    }

    [Fact]
    public void Execute_Remove_PropagatesIndexAndValue()
    {
        FakeNode root = FakeNode.Object("root", ("Set", FakeNode.Scalar("Set")));
        FakeVisitor visitor = new(root);

        TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Remove,
                fieldPath: "Set",
                index: 3,
                indexPath: null,
                descent: null,
                value: null),
            out _);

        Assert.Equal(new[] { "Remove:Set[3]=<none>" }, visitor.Calls);
    }

    [Fact]
    public void Execute_Clear_DispatchesToTryClear()
    {
        FakeNode root = FakeNode.Object("root", ("List", FakeNode.Scalar("List")));
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Clear,
                fieldPath: "List",
                index: null,
                indexPath: null,
                descent: null,
                value: null),
            out _);

        Assert.Equal(OperationResult.Applied, result);
        Assert.Equal(new[] { "Clear:List" }, visitor.Calls);
    }

    [Fact]
    public void Execute_NestedFieldPath_WalksInnerSegments()
    {
        FakeNode inner = FakeNode.Object("Inner", ("Speed", FakeNode.Scalar("Speed")));
        FakeNode root = FakeNode.Object("root", ("Inner", inner));
        FakeVisitor visitor = new(root);

        TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Inner.Speed",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(42)),
            out _);

        Assert.Equal(
            new[]
            {
                "Enter:root.Inner",
                "ReadField:Inner",
                "SetScalar:Speed=42",
            },
            visitor.Calls);
    }

    [Fact]
    public void Execute_Descent_ScalarStepReadsThenCasts()
    {
        FakeNode condition = FakeNode.Object("Condition", ("Threshold", FakeNode.Scalar("Threshold")));
        FakeNode root = FakeNode.Object("root", ("Condition", condition));
        FakeVisitor visitor = new(root);

        TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Threshold",
                index: null,
                indexPath: null,
                descent: new List<TemplateDescentStep>
                {
                    new() { Field = "Condition", Index = null, Subtype = "MoraleCondition" },
                },
                value: Int32(50)),
            out _);

        Assert.Equal(
            new[]
            {
                "Enter:root.Condition",
                "ReadField:Condition",
                "DescendScalar:Condition->MoraleCondition",
                "SetScalar:Threshold=50",
            },
            visitor.Calls);
    }

    [Fact]
    public void Execute_Descent_ElementStepDispatchesToTryDescendElement()
    {
        FakeNode root = FakeNode.Object("root", ("Handlers", FakeNode.Scalar("Handlers")));
        FakeVisitor visitor = new(root);

        TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Trigger",
                index: null,
                indexPath: null,
                descent: new List<TemplateDescentStep>
                {
                    new() { Field = "Handlers", Index = 2, Subtype = "ChangeProperty" },
                },
                value: Int32(0)),
            out _);

        Assert.Equal(
            new[]
            {
                "Enter:root.Handlers",
                "DescendElement:Handlers[2]->ChangeProperty",
                "SetScalar:Trigger=0",
            },
            visitor.Calls);
    }

    [Fact]
    public void Execute_MalformedPath_BracketOnNonTerminalSegmentFailsParse()
    {
        FakeVisitor visitor = new(FakeNode.Object("root"));

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            visitor.Root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "List[0].Field",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(0)),
            out string? error);

        Assert.Equal(OperationResult.MemberMissing, result);
        Assert.Contains("non-terminal", error);
        Assert.Empty(visitor.Calls);
        Assert.Equal(OperationResult.MemberMissing, visitor.LastCompleted);
    }

    [Fact]
    public void Execute_MissingField_VisitorReturnsMemberMissing()
    {
        FakeNode root = FakeNode.Object("root");
        FakeVisitor visitor = new(root);

        OperationResult result = TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Missing.Inner",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(1)),
            out string? error);

        Assert.Equal(OperationResult.MemberMissing, result);
        Assert.Contains("Missing", error);
    }

    [Fact]
    public void Execute_OnCompleted_FiresExactlyOncePerCall()
    {
        FakeNode root = FakeNode.Object("root", ("Speed", FakeNode.Scalar("Speed")));
        FakeVisitor visitor = new(root);

        TemplateOperationWalker.Execute(
            visitor,
            root,
            new TemplateOperationView(
                CompiledTemplateOp.Set,
                fieldPath: "Speed",
                index: null,
                indexPath: null,
                descent: null,
                value: Int32(1)),
            out _);

        Assert.Equal(1, visitor.CompletedCount);
        Assert.Equal(OperationResult.Applied, visitor.LastCompleted);
    }

    private static CompiledTemplateValue Int32(int value) =>
        new() { Kind = CompiledTemplateValueKind.Int32, Int32 = value };

    private sealed class FakeNode
    {
        public string Name { get; init; } = string.Empty;
        public Dictionary<string, FakeNode> Children { get; init; } = new(StringComparer.Ordinal);

        public static FakeNode Object(string name, params (string field, FakeNode child)[] children)
        {
            FakeNode node = new() { Name = name };
            foreach (var (field, child) in children)
                node.Children[field] = child;
            return node;
        }

        public static FakeNode Scalar(string name) => new() { Name = name };
    }

    private sealed class FakeVisitor : IFieldVisitor<FakeNode>
    {
        public FakeVisitor(FakeNode root) { Root = root; }

        public FakeNode Root { get; }
        public List<string> Calls { get; } = new();
        public int CompletedCount { get; private set; }
        public OperationResult LastCompleted { get; private set; }

        public OperationResult SetScalarResult { get; set; } = OperationResult.Applied;
        public OperationResult SetCellResult { get; set; } = OperationResult.Applied;

        public OperationResult TryReadField(FakeNode parent, string fieldName, out FakeNode child, out string? error)
        {
            Calls.Add($"ReadField:{fieldName}");
            if (!parent.Children.TryGetValue(fieldName, out FakeNode? c))
            {
                child = null!;
                error = $"field '{fieldName}' missing";
                return OperationResult.MemberMissing;
            }
            child = c;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryDescendScalar(FakeNode current, string? subtype, out FakeNode descended, out string? error)
        {
            Calls.Add($"DescendScalar:{current.Name}->{subtype ?? "<none>"}");
            descended = current;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryDescendElement(FakeNode parent, string fieldName, int index, string? subtype, out FakeNode descended, out string? error)
        {
            Calls.Add($"DescendElement:{fieldName}[{index}]->{subtype ?? "<none>"}");
            descended = parent;
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TrySetScalar(FakeNode parent, string fieldName, CompiledTemplateValue value, out string? error)
        {
            Calls.Add($"SetScalar:{fieldName}={FormatValue(value)}");
            error = null;
            return SetScalarResult;
        }

        public OperationResult TrySetElement(FakeNode parent, string fieldName, int index, CompiledTemplateValue value, out string? error)
        {
            Calls.Add($"SetElement:{fieldName}[{index}]={FormatValue(value)}");
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TrySetCell(FakeNode parent, string fieldName, IReadOnlyList<int> indexPath, CompiledTemplateValue value, out string? error)
        {
            Calls.Add($"SetCell:{fieldName}[{string.Join(",", indexPath)}]={FormatValue(value)}");
            error = SetCellResult == OperationResult.NotSupported ? "fake says no" : null;
            return SetCellResult;
        }

        public OperationResult TryAppend(FakeNode parent, string fieldName, CompiledTemplateValue value, out string? error)
        {
            Calls.Add($"Append:{fieldName}={FormatValue(value)}");
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryInsertAt(FakeNode parent, string fieldName, int index, CompiledTemplateValue value, out string? error)
        {
            Calls.Add($"InsertAt:{fieldName}[{index}]={FormatValue(value)}");
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryRemove(FakeNode parent, string fieldName, int? index, CompiledTemplateValue? value, out string? error)
        {
            Calls.Add($"Remove:{fieldName}[{(index.HasValue ? index.Value.ToString() : "<none>")}]={(value is null ? "<none>" : FormatValue(value))}");
            error = null;
            return OperationResult.Applied;
        }

        public OperationResult TryClear(FakeNode parent, string fieldName, out string? error)
        {
            Calls.Add($"Clear:{fieldName}");
            error = null;
            return OperationResult.Applied;
        }

        public void OnEnterSegment(FakeNode parent, string segmentName)
        {
            Calls.Add($"Enter:{parent.Name}.{segmentName}");
        }

        public void OnCompleted(OperationResult result)
        {
            CompletedCount++;
            LastCompleted = result;
        }

        private static string FormatValue(CompiledTemplateValue value) => value.Kind switch
        {
            CompiledTemplateValueKind.Int32 => value.Int32?.ToString() ?? "0",
            CompiledTemplateValueKind.Boolean => value.Boolean?.ToString() ?? "false",
            CompiledTemplateValueKind.String => value.String ?? "<null>",
            _ => value.Kind.ToString(),
        };
    }
}
