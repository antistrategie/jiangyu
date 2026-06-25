using System.Text.Json;
using Jiangyu.Loader.Sdk.State;
using Xunit;

namespace Jiangyu.Loader.Tests.Sdk;

public class PersistentModStateTests
{
    public sealed class Box
    {
        public int N { get; set; }
    }

    // A sidecar is a { type-full-name: blob } map. Helper to build one for a single type.
    private static string Sidecar(string blobJson)
        => $"{{ {JsonSerializer.Serialize(typeof(Box).FullName)}: {blobJson} }}";

    [Fact]
    public void Get_realises_a_valid_blob()
    {
        var state = new PersistentModState();
        state.Load(Sidecar("{\"N\":7}"));
        Assert.Equal(7, state.Get<Box>().N);
    }

    [Fact]
    public void Get_returns_the_same_live_instance_so_mutations_persist()
    {
        var state = new PersistentModState();
        var first = state.Get<Box>();
        first.N = 5;
        Assert.Same(first, state.Get<Box>());
        Assert.Equal(5, state.Get<Box>().N);
    }

    [Fact]
    public void Corrupt_blob_resets_to_a_cached_default_that_persists_and_is_saved()
    {
        var state = new PersistentModState();
        // A blob that cannot deserialise into Box (a bare number, not an object).
        state.Load(Sidecar("123"));

        var box = state.Get<Box>();
        Assert.Equal(0, box.N);                 // reset to a fresh default
        box.N = 9;                              // mutate the live instance

        Assert.Same(box, state.Get<Box>());     // cached, not a throwaway
        Assert.Equal(9, state.Get<Box>().N);    // the mutation stuck

        // The reset (mutated) state is what saves, replacing the unreadable original bytes.
        var reloaded = new PersistentModState();
        reloaded.Load(state.Serialize());
        Assert.Equal(9, reloaded.Get<Box>().N);
    }

    [Fact]
    public void Clear_drops_all_state()
    {
        var state = new PersistentModState();
        state.Load(Sidecar("{\"N\":3}"));
        state.Clear();
        Assert.False(state.HasState);
        Assert.Equal(0, state.Get<Box>().N);
    }
}
