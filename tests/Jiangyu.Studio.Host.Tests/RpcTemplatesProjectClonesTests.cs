using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

/// <summary>
/// Tests for the project-clones scanner that backs <c>templatesProjectClones</c>
/// and the bank-resolver broadening in <c>templatesParse</c>. The scanner
/// caches per-file parses keyed by mtime so the editor's debounced
/// templatesParse path doesn't re-parse every <c>templates/*.kdl</c> on
/// each keystroke.
///
/// Each test gets a fresh temp project so the scanner's static cache
/// resets to that root (different RpcContext.ProjectRoot triggers a
/// cache clear). xUnit serialises tests within a class by default which
/// also keeps the shared static cache stable test-to-test.
/// </summary>
[Collection("ProjectClonesCacheCollection")]
public class RpcTemplatesProjectClonesTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _templatesDir;
    private readonly string? _previousProjectRoot;

    public RpcTemplatesProjectClonesTests()
    {
        _projectDir = Path.Combine(
            Path.GetTempPath(),
            "jiangyu-clones-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
        _templatesDir = Path.Combine(_projectDir, "templates");
        Directory.CreateDirectory(_templatesDir);
        _previousProjectRoot = RpcContext.ProjectRoot;
        RpcContext.ProjectRoot = Path.GetFullPath(_projectDir);
    }

    public void Dispose()
    {
        RpcContext.ProjectRoot = _previousProjectRoot;
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    private string WriteTemplate(string relativeName, string contents)
    {
        var path = Path.Combine(_templatesDir, relativeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private static IReadOnlyList<(string type, string id, string file)> ReadClones(JsonElement response)
    {
        var entries = response.GetProperty("clones");
        var list = new List<(string, string, string)>();
        foreach (var entry in entries.EnumerateArray())
        {
            list.Add((
                entry.GetProperty("templateType").GetString()!,
                entry.GetProperty("id").GetString()!,
                entry.GetProperty("file").GetString()!));
        }
        return list;
    }

    [Fact]
    public void NoProjectRoot_ReturnsEmpty()
    {
        // Welcome screen RPCs dispatch before a project is open; the
        // dropdown must render cleanly rather than throwing.
        RpcContext.ProjectRoot = null;
        var response = RpcHandlers.TemplatesProjectClones(null);
        Assert.Empty(ReadClones(response));
    }

    [Fact]
    public void NoTemplatesDir_ReturnsEmpty()
    {
        // Fresh project before the modder has authored anything.
        Directory.Delete(_templatesDir, recursive: true);
        var response = RpcHandlers.TemplatesProjectClones(null);
        Assert.Empty(ReadClones(response));
    }

    [Fact]
    public void EnumeratesClonesAcrossMultipleFiles()
    {
        WriteTemplate("voicelines.kdl", """
            clone "SoundBank" from="tactical_barks_jeansy_va" id="voymastina_va" {
                clear "sounds"
            }
            """);
        WriteTemplate("squad_leader.kdl", """
            patch "Soldier" "S_Voymastina" {
            }
            clone "Attack" from="Punch" id="VoymastinaPunch" {
            }
            """);
        var response = RpcHandlers.TemplatesProjectClones(null);
        var clones = ReadClones(response).OrderBy(c => c.id).ToList();
        Assert.Equal(2, clones.Count);
        Assert.Contains(clones, c => c is { type: "SoundBank", id: "voymastina_va" });
        Assert.Contains(clones, c => c is { type: "Attack", id: "VoymastinaPunch" });
    }

    [Fact]
    public void CacheHit_UnchangedFileSkipsReparse()
    {
        // Write the original, capture mtime, observe scanner output.
        var file = WriteTemplate("voicelines.kdl", """
            clone "SoundBank" from="jeansy_va" id="ORIGINAL_NAME" {
            }
            """);
        var mtime = File.GetLastWriteTimeUtc(file);
        var first = RpcHandlers.TemplatesProjectClones(null);
        Assert.Contains(ReadClones(first), c => c.id == "ORIGINAL_NAME");

        // Overwrite the file's content but restore the prior mtime. The
        // cache keys on mtime, so it should return the stale parsed
        // entry — proving we didn't re-parse the file.
        File.WriteAllText(file, """
            clone "SoundBank" from="jeansy_va" id="CHANGED_BEHIND_BACK" {
            }
            """);
        File.SetLastWriteTimeUtc(file, mtime);

        var second = RpcHandlers.TemplatesProjectClones(null);
        Assert.Contains(ReadClones(second), c => c.id == "ORIGINAL_NAME");
        Assert.DoesNotContain(ReadClones(second), c => c.id == "CHANGED_BEHIND_BACK");
    }

    [Fact]
    public void CacheInvalidates_OnMtimeChange()
    {
        // Original parse populates the cache.
        var file = WriteTemplate("voicelines.kdl", """
            clone "SoundBank" from="jeansy_va" id="ORIGINAL_NAME" {
            }
            """);
        var first = RpcHandlers.TemplatesProjectClones(null);
        Assert.Contains(ReadClones(first), c => c.id == "ORIGINAL_NAME");

        // Update the content AND bump the mtime — the cache key shifts
        // so the scanner re-parses and surfaces the new clone id.
        File.WriteAllText(file, """
            clone "SoundBank" from="jeansy_va" id="UPDATED_NAME" {
            }
            """);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

        var second = ReadClones(RpcHandlers.TemplatesProjectClones(null));
        Assert.Contains(second, c => c.id == "UPDATED_NAME");
        Assert.DoesNotContain(second, c => c.id == "ORIGINAL_NAME");
    }

    [Fact]
    public void EvictsDeletedFiles()
    {
        WriteTemplate("a.kdl", """clone "SoundBank" from="src" id="bank_a" { }""");
        var fileB = WriteTemplate("b.kdl", """clone "SoundBank" from="src" id="bank_b" { }""");
        var initial = ReadClones(RpcHandlers.TemplatesProjectClones(null));
        Assert.Equal(2, initial.Count);

        File.Delete(fileB);
        var afterDelete = ReadClones(RpcHandlers.TemplatesProjectClones(null));
        Assert.Single(afterDelete);
        Assert.Contains(afterDelete, c => c.id == "bank_a");
        Assert.DoesNotContain(afterDelete, c => c.id == "bank_b");
    }

    [Fact]
    public void ProjectSwitch_ResetsCache()
    {
        // Project A: one clone.
        WriteTemplate("voicelines.kdl", """clone "SoundBank" from="src" id="bank_a" { }""");
        var firstA = ReadClones(RpcHandlers.TemplatesProjectClones(null));
        Assert.Contains(firstA, c => c.id == "bank_a");

        // Project B: different temp dir, different clone.
        var projectB = Path.Combine(
            Path.GetTempPath(),
            "jiangyu-clones-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(projectB, "templates"));
        File.WriteAllText(
            Path.Combine(projectB, "templates", "other.kdl"),
            """clone "SoundBank" from="src" id="bank_b" { }""");
        RpcContext.ProjectRoot = Path.GetFullPath(projectB);
        try
        {
            var inB = ReadClones(RpcHandlers.TemplatesProjectClones(null));
            // Project B's scan must NOT include project A's entries —
            // if the cache leaked across roots we'd see "bank_a" here.
            Assert.Contains(inB, c => c.id == "bank_b");
            Assert.DoesNotContain(inB, c => c.id == "bank_a");
        }
        finally
        {
            try { Directory.Delete(projectB, recursive: true); } catch { }
            RpcContext.ProjectRoot = Path.GetFullPath(_projectDir);
        }

        // Back in project A: scan re-populates from disk after the
        // cache reset on the previous switch.
        var backInA = ReadClones(RpcHandlers.TemplatesProjectClones(null));
        Assert.Contains(backInA, c => c.id == "bank_a");
        Assert.DoesNotContain(backInA, c => c.id == "bank_b");
    }

    [Fact]
    public void IgnoresUnparseableFiles()
    {
        // A malformed file shouldn't blow up the whole scan; valid
        // siblings still surface.
        WriteTemplate("broken.kdl", "this is not valid KDL {{{");
        WriteTemplate("good.kdl", """clone "SoundBank" from="src" id="bank_good" { }""");
        var response = ReadClones(RpcHandlers.TemplatesProjectClones(null));
        Assert.Contains(response, c => c.id == "bank_good");
    }

    [Fact]
    public void NestedTemplateSubdirectoriesAreWalked()
    {
        // templates/ scans recursively (SearchOption.AllDirectories).
        WriteTemplate(
            Path.Combine("voymastina", "voicelines.kdl"),
            """clone "SoundBank" from="jeansy_va" id="nested_bank" { }""");
        var response = ReadClones(RpcHandlers.TemplatesProjectClones(null));
        Assert.Contains(response, c => c.id == "nested_bank");
    }
}

[CollectionDefinition("ProjectClonesCacheCollection", DisableParallelization = true)]
public class ProjectClonesCacheCollection
{
    // Tests in this collection share the static project-clones cache
    // inside RpcHandlers, so xUnit runs them serially. Without this,
    // parallel tests racing on RpcContext.ProjectRoot would produce
    // flaky cache behaviour.
}
