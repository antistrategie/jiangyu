using Jiangyu.Shared.State;
using Xunit;

namespace Jiangyu.Loader.Tests.Sdk;

/// <summary>
/// The save file a mod state sidecar attaches to. The game names a new save from a snake-cased
/// fold of the typed name, so the sidecar must follow the fold rather than the raw name, and must
/// give up rather than guess when the fold yields no file.
/// </summary>
public class SaveSlotResolverTests
{
    private const string Saves = "/Saves";

    // Stands in for the game's fold: names that survive it map to a file, blank folds do not.
    private static string? Derive(string name) =>
        name == ":)" || name == "세이브" ? null : $"{Saves}/{name.Replace(' ', '_').ToLowerInvariant()}.save";

    private static Func<string, bool> Present(params string[] paths) => p => paths.Contains(p);

    [Fact]
    public void ExplicitPathWinsOverTheName()
    {
        var slot = SaveSlotResolver.Resolve(
            $"{Saves}/existing.save", "ignored name", Derive, Present($"{Saves}/existing.save"));

        Assert.Equal($"{Saves}/existing.save", slot);
    }

    [Fact]
    public void NamedSaveResolvesToTheDerivedFileNotTheRawName()
    {
        var slot = SaveSlotResolver.Resolve(
            null, "My Save", Derive, Present($"{Saves}/my_save.save"));

        Assert.Equal($"{Saves}/my_save.save", slot);
    }

    [Theory]
    [InlineData(":)")]
    [InlineData("세이브")]
    public void NameThatFoldsAwayResolvesToNull(string saveGameName)
    {
        var slot = SaveSlotResolver.Resolve(
            null, saveGameName, Derive, Present($"{Saves}/anything.save"));

        Assert.Null(slot);
    }

    [Fact]
    public void DerivedPathWithNoFileBehindItResolvesToNull()
    {
        // What a fold that has drifted from the game's own looks like from here.
        var slot = SaveSlotResolver.Resolve(
            null, "My Save", _ => $"{Saves}/My Save.save", Present($"{Saves}/my_save.save"));

        Assert.Null(slot);
    }

    [Fact]
    public void ExplicitPathWithNoFileBehindItResolvesToNull()
    {
        var slot = SaveSlotResolver.Resolve(
            $"{Saves}/deleted.save", null, Derive, Present($"{Saves}/other.save"));

        Assert.Null(slot);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void PathlessSaveResolvesToNull(string? filePath, string? saveGameName)
    {
        // Autosaves and quicksaves pass neither, and derive nothing: the caller recovers by mtime.
        var slot = SaveSlotResolver.Resolve(
            filePath, saveGameName, _ => throw new InvalidOperationException("must not derive"), Present());

        Assert.Null(slot);
    }
}
