using Jiangyu.Sdk;
using Xunit;

namespace Jiangyu.Codegen.Tests;

/// <summary>
/// Validates the committed, generated <see cref="HookCatalog"/> against the committed
/// SDK context types. Hermetic (no game): catches a manifest that names a payload field
/// the context does not have, or a hook whose name and context type drifted apart, so
/// the catalogue stays a faithful reference without a live mission to confirm it.
/// </summary>
public class HookCatalogTests
{
    [Fact]
    public void Catalog_is_not_empty()
        => Assert.NotEmpty(HookCatalog.All);

    [Fact]
    public void Every_context_type_name_matches_the_hook_name()
    {
        foreach (var hook in HookCatalog.All)
            Assert.Equal(hook.Name + "Context", hook.ContextType.Name);
    }

    [Fact]
    public void Every_payload_field_is_an_init_property_on_the_context()
    {
        foreach (var hook in HookCatalog.All)
            foreach (var field in hook.Payload)
            {
                var property = hook.ContextType.GetProperty(field.Name);
                Assert.True(property is not null,
                    $"{hook.ContextType.Name} has no property '{field.Name}' the catalogue claims it carries.");
            }
    }

    [Fact]
    public void Anchor_is_present_exactly_for_non_synthetic_hooks()
    {
        foreach (var hook in HookCatalog.All)
        {
            if (hook.Kind == HookKind.Synthetic)
                Assert.Equal("", hook.Anchor);
            else
                Assert.NotEqual("", hook.Anchor);
        }
    }

    [Fact]
    public void Layers_are_tactical_or_strategy()
    {
        foreach (var hook in HookCatalog.All)
            Assert.Contains(hook.Layer, new[] { "Tactical", "Strategy" });
    }
}
