using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.IO.Files;
using AssetRipper.Processing;

namespace Jiangyu.Core.Assets;

/// <summary>
/// Jiangyu-owned <see cref="ExportHandler"/> subclass that exposes
/// AssetRipper's subset-export path without patching the upstream
/// <c>ExportHandler.cs</c> file. The wrapper reaches into the protected
/// <c>Settings</c> property and <c>BeforeExport</c> hook, mirroring the
/// shape of the base class's public <c>Export</c> method and delegating
/// the per-collection write skip to
/// <see cref="ProjectExporter.ExportSubset"/> (the only piece that has to
/// live inside AssetRipper because it touches private event invokers and
/// <c>CreateCollections</c>).
///
/// <para>Skips the post-exporters (ProjectVersion.txt, package manifest,
/// streaming assets, DLLs, path-id map) because they operate at the
/// whole-project level and don't make sense for a single-prefab
/// extraction.</para>
/// </summary>
public sealed class JiangyuExportHandler : ExportHandler
{
    public JiangyuExportHandler(FullConfiguration settings) : base(settings) { }

    public void ExportSubset(GameData gameData, string outputPath, FileSystem fileSystem, ISet<IUnityObjectBase> assetsToExport)
    {
        Logger.Info(LogCategory.Export, $"Exporting subset of {assetsToExport.Count} asset(s) to {outputPath}");
        Settings.ExportRootPath = outputPath;
        Settings.SetProjectSettings(gameData.ProjectVersion);

        ProjectExporter projectExporter = new(Settings, gameData.AssemblyManager);
        BeforeExport(projectExporter);
        projectExporter.DoFinalOverrides(Settings);

        // Salt exported GUIDs with this prefab's directory name. Shared dependencies
        // (a shader or physic material used by several prefabs) are exported into one
        // subset dir per prefab; an unsalted GUID is identical across those copies, so
        // importing them together makes Unity flag a duplicate and reassign random GUIDs,
        // breaking the references on every rip. The salt keeps each copy distinct and stable.
        //
        // Assets whose (ClassID, name) is unique inside this subset additionally get a
        // name-derived GUID rather than a PathID-derived one. Mod content committed to a
        // repo references these exports by GUID, and PathIDs reshuffle whenever a game
        // update re-serialises the source files, so name-keyed GUIDs are the only ones
        // that survive an update. Duplicate names keep the PathID hash: a name-derived
        // GUID could swap between the duplicates from one build to the next.
        var nameCounts = new Dictionary<(int ClassID, string Name), int>();
        foreach (IUnityObjectBase asset in assetsToExport)
        {
            string name = asset.GetBestName();
            if (string.IsNullOrEmpty(name)) continue;
            var key = (asset.ClassID, name);
            nameCounts[key] = nameCounts.GetValueOrDefault(key) + 1;
        }
        var previousNamespace = ExportCollection.GuidNamespace;
        var previousStableNames = ExportCollection.StableNameKeys;
        ExportCollection.GuidNamespace = fileSystem.Path.GetFileName(outputPath.TrimEnd('/', '\\'));
        ExportCollection.StableNameKeys = nameCounts
            .Where(pair => pair.Value == 1)
            .Select(pair => pair.Key)
            .ToHashSet();
        try
        {
            projectExporter.ExportSubset(gameData.GameBundle, Settings, fileSystem, assetsToExport);
        }
        finally
        {
            ExportCollection.GuidNamespace = previousNamespace;
            ExportCollection.StableNameKeys = previousStableNames;
        }

        Logger.Info(LogCategory.Export, "Finished subset export");
    }
}
