using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;

namespace AssetRipper.Export.UnityProjects;

/// <summary>
/// Jiangyu-owned additions to <see cref="ProjectExporter"/>. Lives in its
/// own partial so future <c>git subtree pull</c>s from upstream don't
/// touch this file. The members here reach into AssetRipper's
/// non-public surface (event invokers, <c>CreateCollections</c>, the
/// <c>ProjectAssetContainer</c> ctor visibility) and therefore can't be
/// implemented from a downstream assembly.
/// </summary>
partial class ProjectExporter
{
	/// <summary>
	/// Exports only the export collections that contain at least one of the
	/// assets in <paramref name="assetsToExport"/>. All collections in the
	/// bundle are still <i>created</i> so the cross-reference container (GUID
	/// lookup) resolves when the kept collections serialise; only the file
	/// write is skipped for collections outside the requested subset.
	/// Callers must transitively walk dependencies into
	/// <paramref name="assetsToExport"/>, otherwise produced files will carry
	/// references to assets that were not written.
	///
	/// Backs <c>jiangyu unity import-prefab &lt;name&gt;</c>, which extracts
	/// a single prefab plus its dependency closure without paying the cost
	/// of a full game-project export.
	/// </summary>
	public void ExportSubset(GameBundle fileCollection, CoreConfiguration options, FileSystem fileSystem, ISet<IUnityObjectBase> assetsToExport)
	{
		EventExportPreparationStarted?.Invoke();
		List<IExportCollection> collections = CreateCollections(fileCollection);
		EventExportPreparationFinished?.Invoke();

		EventExportStarted?.Invoke();
		ProjectAssetContainer container = new ProjectAssetContainer(this, options, fileCollection.FetchAssets(), collections);

		int kept = 0;
		for (int i = 0; i < collections.Count; i++)
		{
			IExportCollection collection = collections[i];
			container.CurrentCollection = collection;
			if (!collection.Exportable)
			{
				EventExportProgressUpdated?.Invoke(i, collections.Count);
				continue;
			}
			bool collectionMatches = false;
			foreach (IUnityObjectBase asset in collection.Assets)
			{
				if (assetsToExport.Contains(asset))
				{
					collectionMatches = true;
					break;
				}
			}
			if (!collectionMatches)
			{
				EventExportProgressUpdated?.Invoke(i, collections.Count);
				continue;
			}
			kept++;
			Logger.Info(LogCategory.ExportProgress, $"({kept}) Exporting '{collection.Name}'");
			bool exportedSuccessfully = collection.Export(container, options.ProjectRootPath, fileSystem);
			if (!exportedSuccessfully)
			{
				Logger.Warning(LogCategory.ExportProgress, $"Failed to export '{collection.Name}' ({collection.GetType().Name})");
			}
			EventExportProgressUpdated?.Invoke(i, collections.Count);
		}
		EventExportFinished?.Invoke();
	}
}
