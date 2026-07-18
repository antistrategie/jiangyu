using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.SourceGenerated.Classes.ClassID_1034;
using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.Export.UnityProjects;

public class AssetExportCollection<T> : ExportCollection where T : IUnityObjectBase
{
	public AssetExportCollection(IAssetExporter assetExporter, T asset)
	{
		AssetExporter = assetExporter ?? throw new ArgumentNullException(nameof(assetExporter));
		Asset = asset ?? throw new ArgumentNullException(nameof(asset));
		GUID = ComputeStableGuid(asset);
	}

	public override bool Export(IExportContainer container, string projectDirectory, FileSystem fileSystem)
	{
		string subPath = fileSystem.Path.Join(projectDirectory, FileSystem.FixInvalidPathCharacters(Asset.GetBestDirectory()));
		string fileName = GetUniqueFileName(Asset, subPath, fileSystem);

		fileSystem.Directory.Create(subPath);

		string filePath = fileSystem.Path.Join(subPath, fileName);
		bool result = ExportInner(container, filePath, projectDirectory, fileSystem);
		if (result)
		{
			Meta meta = new Meta(GUID, CreateImporter(container));
			ExportMeta(container, meta, filePath, fileSystem);
			return true;
		}
		return false;
	}

	public override bool Contains(IUnityObjectBase asset)
	{
		return Asset.AssetInfo == asset.AssetInfo;
	}

	public override long GetExportID(IExportContainer container, IUnityObjectBase asset)
	{
		if (asset.AssetInfo == Asset.AssetInfo)
		{
			return ExportIdHandler.GetMainExportID(Asset);
		}
		throw new ArgumentException(null, nameof(asset));
	}

	public override MetaPtr CreateExportPointer(IExportContainer container, IUnityObjectBase asset, bool isLocal)
	{
		long exportID = GetExportID(container, asset);
		return isLocal ?
			new MetaPtr(exportID) :
			new MetaPtr(exportID, GUID, AssetExporter.ToExportType(Asset));
	}

	/// <summary>
	/// 
	/// </summary>
	/// <param name="container"></param>
	/// <param name="filePath">The full path to the exported asset destination</param>
	/// <param name="dirPath">The full path to the project export directory</param>
	/// <returns>True if export was successful, false otherwise</returns>
	protected virtual bool ExportInner(IExportContainer container, string filePath, string dirPath, FileSystem fileSystem)
	{
		return AssetExporter.Export(container, Asset, filePath, fileSystem);
	}

	protected virtual IUnityObjectBase CreateImporter(IExportContainer container)
	{
		INativeFormatImporter importer = NativeFormatImporter.Create(container.File, container.ExportVersion);
		importer.MainObjectFileID = GetExportID(container, Asset);
		if (importer.Has_AssetBundleName_R() && Asset.AssetBundleName is not null)
		{
			importer.AssetBundleName_R = Asset.AssetBundleName;
		}
		return importer;
	}

	public override UnityGuid GUID { get; }
	public override IAssetExporter AssetExporter { get; }
	public override AssetCollection File => Asset.Collection;
	public override IEnumerable<IUnityObjectBase> Assets
	{
		get { yield return Asset; }
	}
	public override string Name => Asset.GetBestName();
	public T Asset { get; }

	/// <summary>
	/// Deterministic GUID derived from the asset's identity inside the source bundle, salted with
	/// <see cref="ExportCollection.GuidNamespace"/> so a shared source asset exported into more than
	/// one subset directory gets a distinct (but rip-stable) GUID per directory rather than a
	/// colliding one that Unity re-randomises on import. Assets listed in
	/// <see cref="ExportCollection.StableNameKeys"/> hash their name instead of collection and
	/// PathID, which also keeps the GUID stable across game builds.
	/// </summary>
	protected virtual UnityGuid ComputeStableGuid(T asset)
	{
		string name = asset.GetBestName();
		if (StableNameKeys.Contains((asset.ClassID, name)))
		{
			return ComputeNameStableGuid(GuidNamespace, name, asset.ClassID);
		}
		string collectionName = GuidNamespace.Length == 0
			? asset.Collection.Name
			: $"{GuidNamespace}/{asset.Collection.Name}";
		return ComputeStableGuid(collectionName, asset.PathID, asset.ClassID);
	}

	/// <summary>
	/// Name-keyed variant of <see cref="ComputeStableGuid(string, long, int)"/> for assets in
	/// <see cref="ExportCollection.StableNameKeys"/>. The "name:" prefix separates the two hash
	/// domains so a name can never collide with a collection name.
	/// </summary>
	public static UnityGuid ComputeNameStableGuid(string guidNamespace, string name, int classId)
	{
		string salted = guidNamespace.Length == 0 ? name : $"{guidNamespace}/{name}";
		return ComputeStableGuid($"name:{salted}", 0, classId);
	}

	/// <summary>
	/// MD5 of UTF-8 collection name, little-endian int64 PathID, and little-endian int32 ClassID.
	/// </summary>
	public static UnityGuid ComputeStableGuid(string collectionName, long pathId, int classId)
	{
		ReadOnlySpan<byte> nameBytes = Encoding.UTF8.GetBytes(collectionName);
		Span<byte> source = stackalloc byte[nameBytes.Length + sizeof(long) + sizeof(int)];
		nameBytes.CopyTo(source);
		BinaryPrimitives.WriteInt64LittleEndian(source.Slice(nameBytes.Length, sizeof(long)), pathId);
		BinaryPrimitives.WriteInt32LittleEndian(source.Slice(nameBytes.Length + sizeof(long), sizeof(int)), classId);
		return UnityGuid.Md5Hash(source);
	}
}
