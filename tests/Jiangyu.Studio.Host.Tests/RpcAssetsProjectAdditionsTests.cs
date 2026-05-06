using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

/// <summary>
/// Tests for the <c>assetsProjectAdditions</c> RPC handler that the
/// asset-reference picker calls to enumerate project-shipped additions.
/// The handler walks <c>assets/additions/&lt;category&gt;/</c> filtered by
/// the destination Unity type so the dropdown only surfaces files that can
/// actually resolve onto the field being edited.
/// </summary>
public class RpcAssetsProjectAdditionsTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string? _previousProjectRoot;

    public RpcAssetsProjectAdditionsTests()
    {
        _projectDir = Path.Combine(
            Path.GetTempPath(),
            "jiangyu-additions-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
        _previousProjectRoot = RpcContext.ProjectRoot;
        RpcContext.ProjectRoot = Path.GetFullPath(_projectDir);
    }

    public void Dispose()
    {
        RpcContext.ProjectRoot = _previousProjectRoot;
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    private static JsonElement Invoke(string unityType)
    {
        var parameters = JsonSerializer.SerializeToElement(new { unityType });
        return RpcHandlers.AssetsProjectAdditions(parameters);
    }

    private static IReadOnlyList<(string name, string file)> ReadAdditions(JsonElement response)
    {
        var entries = response.GetProperty("additions");
        var list = new List<(string, string)>();
        foreach (var entry in entries.EnumerateArray())
        {
            list.Add((entry.GetProperty("name").GetString()!, entry.GetProperty("file").GetString()!));
        }
        return list;
    }

    [Fact]
    public void NoProjectRoot_ReturnsEmpty()
    {
        // Studio dispatches RPCs even before a project is open (the welcome
        // screen has no project context). Returning empty rather than
        // throwing keeps the picker render-safe in that window.
        RpcContext.ProjectRoot = null;
        var response = Invoke("Sprite");
        Assert.Empty(ReadAdditions(response));
    }

    [Fact]
    public void NoAdditionsFolder_ReturnsEmpty()
    {
        // Mod projects are author-owned; the additions tree only exists if
        // the modder has placed files. The picker still runs in projects
        // that ship only replacements or only template patches.
        var response = Invoke("Sprite");
        Assert.Empty(ReadAdditions(response));
    }

    [Fact]
    public void Sprites_FlatFile_LogicalNameIsStem()
    {
        WriteAddition("sprites", "icon.png");

        var response = Invoke("Sprite");
        var entries = ReadAdditions(response);

        Assert.Single(entries);
        Assert.Equal("icon", entries[0].name);
        Assert.Equal("assets/additions/sprites/icon.png", entries[0].file);
    }

    [Fact]
    public void Sprites_NestedFile_LogicalNameKeepsForwardSlash()
    {
        WriteAddition("sprites", Path.Combine("lrm5", "icon.png"));

        var response = Invoke("Sprite");
        var entries = ReadAdditions(response);

        Assert.Single(entries);
        Assert.Equal("lrm5/icon", entries[0].name);
        Assert.Equal("assets/additions/sprites/lrm5/icon.png", entries[0].file);
    }

    [Fact]
    public void Sprites_MultipleEntries_SortedByName()
    {
        WriteAddition("sprites", Path.Combine("z", "last.png"));
        WriteAddition("sprites", "alpha.png");
        WriteAddition("sprites", Path.Combine("m", "middle.png"));

        var response = Invoke("Sprite");
        var entries = ReadAdditions(response);

        Assert.Equal(["alpha", "m/middle", "z/last"], entries.Select(e => e.name).ToArray());
    }

    [Fact]
    public void Sprites_IgnoresNonImageExtensions()
    {
        // Stray files in the category folder (e.g. a .gitkeep, a backup
        // .bak, an editor metadata file) are not buildable as additions
        // and must not appear in the picker, otherwise selecting one
        // would yield a runtime "asset not found" error.
        WriteAddition("sprites", "icon.png");
        WriteAddition("sprites", "notes.txt");
        WriteAddition("sprites", ".gitkeep");

        var response = Invoke("Sprite");
        var entries = ReadAdditions(response);

        Assert.Equal(["icon"], entries.Select(e => e.name).ToArray());
    }

    [Fact]
    public void Audio_ReturnsAudioFiles()
    {
        WriteAddition("audio", "shot.wav");
        WriteAddition("audio", "music.ogg");
        WriteAddition("audio", "ignored.png");

        var response = Invoke("AudioClip");
        var entries = ReadAdditions(response);

        Assert.Equal(["music", "shot"], entries.Select(e => e.name).ToArray());
    }

    [Fact]
    public void Textures_ScansTextureFolderNotSprites()
    {
        // Categories are independent. A modder authoring a Sprite addition
        // and a Texture2D addition with overlapping logical names lands one
        // file in each category folder; the picker for a Texture2D field
        // must only surface the textures/ entry.
        WriteAddition("sprites", "shared.png");
        WriteAddition("textures", "only-texture.png");

        var response = Invoke("Texture2D");
        var entries = ReadAdditions(response);

        Assert.Equal(["only-texture"], entries.Select(e => e.name).ToArray());
    }

    [Fact]
    public void DeferredKind_Mesh_ReturnsEmpty()
    {
        // Mesh / GameObject additions are reserved for the prefab-construction
        // layer; AssetCategory.ForClassName throws for them. The handler
        // catches that and returns empty so the picker simply has nothing
        // to offer rather than surfacing the deferral exception.
        WriteAddition("models", "weapon.glb");

        var response = Invoke("Mesh");
        Assert.Empty(ReadAdditions(response));
    }

    [Fact]
    public void DeferredKind_GameObject_ReturnsEmpty()
    {
        var response = Invoke("GameObject");
        Assert.Empty(ReadAdditions(response));
    }

    [Fact]
    public void UnknownKind_ReturnsEmpty()
    {
        // An asset-reference field whose declared type isn't a Unity asset
        // class (the validator would reject `asset=` in this slot anyway)
        // still gets a clean empty list rather than throwing.
        var response = Invoke("ScriptableObject");
        Assert.Empty(ReadAdditions(response));
    }

    [Fact]
    public void Material_ReturnsEmpty_NoAdditionPipeline()
    {
        // Material is an IsSupported kind for asset references (the loader
        // resolves them via the live game-asset registry), but the compile
        // pipeline doesn't pack additions into a Materials bundle dictionary
        // yet. The handler returns empty so the picker doesn't suggest
        // unbuildable files even when the modder has put PNGs there.
        WriteAddition("materials", "fancy.png");

        var response = Invoke("Material");
        Assert.Empty(ReadAdditions(response));
    }

    [Fact]
    public void MissingUnityType_Throws()
    {
        // The handler requires the destination type so it can pick the
        // category folder; calling without it is an internal contract bug
        // worth surfacing rather than silently returning empty.
        var parameters = JsonSerializer.SerializeToElement(new { });
        Assert.ThrowsAny<Exception>(() => RpcHandlers.AssetsProjectAdditions(parameters));
    }

    private void WriteAddition(string category, string relativePath)
    {
        var full = Path.Combine(_projectDir, "assets", "additions", category, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [0x00]);
    }
}
