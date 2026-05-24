using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Templates;

/// <summary>
/// Post-validation pass that injects deterministic <c>Guid</c> values into
/// composite constructions of conversation-node types when the modder
/// omitted them. ConversationNode subtypes and <c>ConversationNodeContainer</c>
/// carry an <c>int Guid</c> used by MENACE's playback machinery (GOTO/IF
/// jumps, save-state references). Modders rarely care about the specific
/// value, only that each node in a conversation has a distinct one that
/// doesn't collide with the source's Guids.
///
/// The auto-fill derives each Guid via
/// <c>FNV-1a("&lt;scope&gt;#&lt;counter&gt;")</c> where the scope is the
/// patch/clone identifier and the counter increments per composite in
/// declaration order. Stable across rebuilds, distinct within a scope, and
/// distinct from any plausible vanilla Guid (vanilla values come from
/// Unity's editor-time random allocator).
///
/// Runs after <see cref="TemplateCatalogValidator"/> so it sees fully-
/// resolved <c>TypeName</c> values (FQN) and can do straightforward
/// catalog lookups instead of repeating the discriminator dance.
/// </summary>
internal static class NodeGuidAutoFiller
{
    private const string BaseConversationNodeFullName = "Il2CppMenace.Conversations.BaseConversationNode";
    private const string ConversationNodeContainerFullName = "Il2CppMenace.Conversations.ConversationNodeContainer";

    public static void Apply(
        IEnumerable<CompiledTemplatePatch>? patches,
        TemplateTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var baseNodeType = catalog.ResolveType(BaseConversationNodeFullName, out _, out _);
        var containerType = catalog.ResolveType(ConversationNodeContainerFullName, out _, out _);
        if (baseNodeType is null && containerType is null) return;

        if (patches is null) return;
        foreach (var patch in patches)
        {
            var scope = $"{patch.TemplateType ?? "EntityTemplate"}:{patch.TemplateId}";
            var counter = new Counter();
            foreach (var op in patch.Set)
                Walk(op.Value, scope, counter, baseNodeType, containerType, catalog);
        }
    }

    private static void Walk(
        CompiledTemplateValue? value,
        string scope,
        Counter counter,
        Type? baseNodeType,
        Type? containerType,
        TemplateTypeCatalog catalog)
    {
        if (value is null) return;
        if (value.Kind == CompiledTemplateValueKind.Composite && value.Composite is { } composite)
        {
            MaybeInjectGuid(composite, scope, counter, baseNodeType, containerType, catalog);
            foreach (var inner in composite.Operations)
                Walk(inner.Value, scope, counter, baseNodeType, containerType, catalog);
        }
        else if (value.Kind == CompiledTemplateValueKind.HandlerConstruction
            && value.HandlerConstruction is { } handler)
        {
            // Handlers are ScriptableObjects, not ConversationNodes, so the
            // top-level construct doesn't qualify for auto-Guid. Recurse
            // anyway in case a handler holds a nested ConversationNode
            // composite (none in MENACE today, future-proof).
            foreach (var inner in handler.Operations)
                Walk(inner.Value, scope, counter, baseNodeType, containerType, catalog);
        }
    }

    private static void MaybeInjectGuid(
        CompiledTemplateComposite composite,
        string scope,
        Counter counter,
        Type? baseNodeType,
        Type? containerType,
        TemplateTypeCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(composite.TypeName)) return;

        var resolved = catalog.ResolveType(composite.TypeName, out _, out _);
        if (resolved is null) return;

        var qualifies =
            (baseNodeType is not null && baseNodeType.IsAssignableFrom(resolved))
            || (containerType is not null && containerType.IsAssignableFrom(resolved));
        if (!qualifies) return;

        // Modder already set Guid explicitly. Leave it alone.
        foreach (var op in composite.Operations)
        {
            if (op.Op == CompiledTemplateOp.Set
                && string.Equals(op.FieldPath, "Guid", StringComparison.Ordinal))
                return;
        }

        var generated = HashableIdFieldRegistry.Fnv1a32($"{scope}#node_{counter.Next()}");
        composite.Operations.Insert(0, new CompiledTemplateSetOperation
        {
            Op = CompiledTemplateOp.Set,
            FieldPath = "Guid",
            Value = new CompiledTemplateValue
            {
                Kind = CompiledTemplateValueKind.Int32,
                Int32 = generated,
            },
        });
    }

    private sealed class Counter
    {
        private int _value;
        public int Next() => _value++;
    }
}
