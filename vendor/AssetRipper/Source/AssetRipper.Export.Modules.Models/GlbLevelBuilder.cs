using AssetRipper.Assets;
using AssetRipper.Assets.Bundles;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Import.Logging;
using AssetRipper.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_18;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Classes.ClassID_93;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Quaternionf;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Vector3f;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Material;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.UnityTexEnv;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using System.Buffers;

namespace AssetRipper.Export.Modules.Models;

public static class GlbLevelBuilder
{
	public static SceneBuilder Build(IEnumerable<IUnityObjectBase> assets, bool isScene)
	{
		SceneBuilder sceneBuilder = new();
		BuildParameters parameters = new BuildParameters(isScene);

		HashSet<IUnityObjectBase> exportedAssets = new();

		foreach (IUnityObjectBase asset in assets)
		{
			if (!exportedAssets.Contains(asset) && asset is IGameObject or IComponent)
			{
				IGameObject root = GetRoot(asset);
				ITransform rootTransform = root.GetTransform();

				// Pass 1: Build all nodes and register in BoneNodeCache.
				// This ensures canonical skeleton nodes (Root/Hips/...) exist
				// before any SMR tries to resolve joints.
				BuildNodeHierarchy(sceneBuilder, parameters, null, Transformation.Identity, Transformation.Identity, rootTransform);

				// Pass 2: Attach meshes/skins to the already-built nodes.
				// SMRs resolve joints from BoneNodeCache, reusing canonical nodes.
				AttachMeshes(sceneBuilder, parameters, Transformation.Identity, Transformation.Identity, rootTransform);

				foreach (IEditorExtension exportedAsset in root.FetchHierarchy())
				{
					exportedAssets.Add(exportedAsset);
				}
			}
		}

		return sceneBuilder;
	}

	/// <summary>
	/// Pass 1: Build the full node hierarchy without attaching any meshes.
	/// Registers every node in BoneNodeCache keyed by ITransform so that
	/// skinned mesh export can resolve joints to canonical shared nodes
	/// (e.g. Root/Hips) instead of creating per-LOD duplicates.
	/// </summary>
	private static void BuildNodeHierarchy(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder? parentNode, Transformation parentGlobalTransform, Transformation parentGlobalInverseTransform, ITransform transform)
	{
		IGameObject? gameObject = transform.GameObject_C4P;
		if (gameObject is null)
		{
			return;
		}

		Transformation localTransform = transform.ToTransformation();
		Transformation localInverseTransform = transform.ToInverseTransformation();

		NodeBuilder node = parentNode is null ? new NodeBuilder(gameObject.Name) : parentNode.CreateNode(gameObject.Name);
		if (parentNode is not null || parameters.IsScene)
		{
			node.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
				SnapScale(transform.LocalScale_C4.CastToStruct()),
				GlbCoordinateConversion.ToGltfQuaternionConvert(transform.LocalRotation_C4),
				GlbCoordinateConversion.ToGltfVector3Convert(transform.LocalPosition_C4));
		}
		sceneBuilder.AddNode(node);

		// Register in bone cache so SMRs can resolve joints to these canonical nodes
		parameters.BoneNodeCache[transform] = node;

		foreach (ITransform childTransform in transform.Children_C4P.WhereNotNull())
		{
			BuildNodeHierarchy(sceneBuilder, parameters, node, localTransform * parentGlobalTransform, parentGlobalInverseTransform * localInverseTransform, childTransform);
		}
	}

	/// <summary>
	/// Pass 2: Walk the hierarchy again, attaching meshes to already-built nodes.
	/// Skinned meshes resolve joints from BoneNodeCache (populated by pass 1).
	/// </summary>
	private static void AttachMeshes(SceneBuilder sceneBuilder, BuildParameters parameters, Transformation parentGlobalTransform, Transformation parentGlobalInverseTransform, ITransform transform)
	{
		IGameObject? gameObject = transform.GameObject_C4P;
		if (gameObject is null)
		{
			return;
		}

		Transformation localTransform = transform.ToTransformation();
		Transformation localInverseTransform = transform.ToInverseTransformation();
		Transformation globalTransform = localTransform * parentGlobalTransform;
		Transformation globalInverseTransform = parentGlobalInverseTransform * localInverseTransform;

		if (!parameters.BoneNodeCache.TryGetValue(transform, out NodeBuilder? node))
		{
			return;
		}

		// Handle SkinnedMeshRenderer (bones, skinned meshes) first
		if (gameObject.TryGetComponent(out ISkinnedMeshRenderer? skinnedRenderer))
		{
			IMesh? skinnedMesh = skinnedRenderer.MeshP;
			if (skinnedMesh is not null && skinnedMesh.IsSet() && parameters.TryGetOrMakeMeshData(skinnedMesh, out MeshData skinnedMeshData))
			{
				// Rename container node if it collides with the mesh name
				// so the mesh keeps the clean name in Blender's namespace
				if (node.Name == skinnedMesh.Name)
				{
					node.Name = $"{node.Name}_container";
				}

				AddSkinnedMeshToScene(sceneBuilder, parameters, node, skinnedMesh, skinnedMeshData, skinnedRenderer, globalTransform, globalInverseTransform);
			}
		}
		// Handle static MeshFilter + MeshRenderer
		else if (gameObject.TryGetComponent(out IMeshFilter? meshFilter)
			&& meshFilter.TryGetMesh(out IMesh? mesh)
			&& mesh.IsSet()
			&& parameters.TryGetOrMakeMeshData(mesh, out MeshData meshData))
		{
			if (gameObject.TryGetComponent(out IRenderer? meshRenderer))
			{
				if (ReferencesDynamicMesh(meshRenderer))
				{
					AddDynamicMeshToScene(sceneBuilder, parameters, node, mesh, meshData, new MaterialList(meshRenderer));
				}
				else
				{
					int[] subsetIndices = GetSubsetIndices(meshRenderer);
					AddStaticMeshToScene(sceneBuilder, parameters, node, mesh, meshData, subsetIndices, new MaterialList(meshRenderer), globalTransform, globalInverseTransform);
				}
			}
		}

		foreach (ITransform childTransform in transform.Children_C4P.WhereNotNull())
		{
			AttachMeshes(sceneBuilder, parameters, localTransform * parentGlobalTransform, parentGlobalInverseTransform * localInverseTransform, childTransform);
		}
	}

	private static void AddDynamicMeshToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder node, IMesh mesh, MeshData meshData, MaterialList materialList)
	{
		AccessListBase<ISubMesh> subMeshes = mesh.SubMeshes;
		(ISubMesh, MaterialBuilder)[] subMeshArray = ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Rent(subMeshes.Count);
		for (int i = 0; i < subMeshes.Count; i++)
		{
			MaterialBuilder materialBuilder = parameters.GetOrMakeMaterial(materialList[i]);
			subMeshArray[i] = (subMeshes[i], materialBuilder);
		}
		ArraySegment<(ISubMesh, MaterialBuilder)> arraySegment = new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshArray, 0, subMeshes.Count);
		IMeshBuilder<MaterialBuilder> subMeshBuilder = GlbSubMeshBuilder.BuildSubMeshes(arraySegment, mesh.Is16BitIndices(), meshData, Transformation.Identity, Transformation.Identity, mesh.Name);
		sceneBuilder.AddRigidMesh(subMeshBuilder, node);
		ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Return(subMeshArray);
	}

	private static void AddStaticMeshToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder node, IMesh mesh, MeshData meshData, int[] subsetIndices, MaterialList materialList, Transformation globalTransform, Transformation globalInverseTransform)
	{
		(ISubMesh, MaterialBuilder)[] subMeshArray = ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Rent(subsetIndices.Length);
		AccessListBase<ISubMesh> subMeshes = mesh.SubMeshes;
		for (int i = 0; i < subsetIndices.Length; i++)
		{
			ISubMesh subMesh = subMeshes[subsetIndices[i]];
			MaterialBuilder materialBuilder = parameters.GetOrMakeMaterial(materialList[i]);
			subMeshArray[i] = (subMesh, materialBuilder);
		}
		ArraySegment<(ISubMesh, MaterialBuilder)> arraySegment = new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshArray, 0, subsetIndices.Length);
		IMeshBuilder<MaterialBuilder> subMeshBuilder = GlbSubMeshBuilder.BuildSubMeshes(arraySegment, mesh.Is16BitIndices(), meshData, globalInverseTransform, globalTransform, mesh.Name);
		sceneBuilder.AddRigidMesh(subMeshBuilder, node);
		ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Return(subMeshArray);
	}

	private static void AddSkinnedMeshToScene(
		SceneBuilder sceneBuilder,
		BuildParameters parameters,
		NodeBuilder node,
		IMesh mesh,
		MeshData meshData,
		ISkinnedMeshRenderer skinnedRenderer,
		Transformation globalTransform,
		Transformation globalInverseTransform)
	{
		try
		{
			var bones = skinnedRenderer.BonesP;
			if (bones.Count == 0)
			{
				AddDynamicMeshToScene(sceneBuilder, parameters, node, mesh, meshData, new MaterialList(skinnedRenderer));
				return;
			}

			// Resolve joint nodes from BoneNodeCache (populated by BuildNodeHierarchy pass 1).
			// All bone transforms should already be canonical hierarchy nodes — no per-SMR
			// skeleton duplication. Primary key is ITransform identity.
			NodeBuilder[] jointNodes = new NodeBuilder[bones.Count];
			for (int i = 0; i < bones.Count; i++)
			{
				ITransform? bone = bones[i];
				if (bone is not null && parameters.BoneNodeCache.TryGetValue(bone, out NodeBuilder? boneNode))
				{
					jointNodes[i] = boneNode;
				}
			}

			// Fill any unresolved slots with placeholders
			for (int i = 0; i < jointNodes.Length; i++)
			{
				jointNodes[i] ??= node.CreateNode($"UnusedBone_{i}");
			}

			// Build the mesh
			AccessListBase<ISubMesh> subMeshes = mesh.SubMeshes;
			(ISubMesh, MaterialBuilder)[] subMeshArray = ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Rent(subMeshes.Count);
			MaterialList materialList = new(skinnedRenderer);
			for (int i = 0; i < subMeshes.Count; i++)
			{
				MaterialBuilder materialBuilder = parameters.GetOrMakeMaterial(materialList[i]);
				subMeshArray[i] = (subMeshes[i], materialBuilder);
			}
			ArraySegment<(ISubMesh, MaterialBuilder)> arraySegment = new(subMeshArray, 0, subMeshes.Count);
			IMeshBuilder<MaterialBuilder> meshBuilder = GlbSubMeshBuilder.BuildSubMeshes(arraySegment, mesh.Is16BitIndices(), meshData, Transformation.Identity, Transformation.Identity, mesh.Name);
			ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Return(subMeshArray);

			sceneBuilder.AddSkinnedMesh(meshBuilder, System.Numerics.Matrix4x4.Identity, jointNodes);
		}
		catch (Exception ex)
		{
			Logger.Warning(LogCategory.Export, $"Failed to export skinned mesh '{mesh.Name}', falling back to rigid: {ex.Message}");
			AddDynamicMeshToScene(sceneBuilder, parameters, node, mesh, meshData, new MaterialList(skinnedRenderer));
		}
	}

	/// <summary>
	/// Snaps near-1.0 scale components to exactly 1.0 to eliminate floating point noise
	/// from matrix decomposition (e.g. 0.9999997 → 1.0).
	/// </summary>
	private static System.Numerics.Vector3 SnapScale(System.Numerics.Vector3 scale)
	{
		const float tolerance = 1e-5f;
		return new System.Numerics.Vector3(
			MathF.Abs(scale.X - 1f) < tolerance ? 1f : scale.X,
			MathF.Abs(scale.Y - 1f) < tolerance ? 1f : scale.Y,
			MathF.Abs(scale.Z - 1f) < tolerance ? 1f : scale.Z);
	}

	private static string GetTransformPath(ITransform transform, IGameObject? root)
	{
		List<string> parts = new();
		ITransform? current = transform;
		while (current is not null)
		{
			IGameObject? go = current.GameObject_C4P;
			if (go is not null)
			{
				if (root is not null && go == root)
				{
					break;
				}
				parts.Add(go.Name);
			}
			current = current.Father_C4P;
		}
		parts.Reverse();
		return string.Join("/", parts);
	}

	private static void TryExportAnimations(IGameObject gameObject, Dictionary<string, NodeBuilder> bonePathToNode)
	{
		try
		{
			IAnimator? animator = FindAnimatorInHierarchy(gameObject);
			if (animator is null)
			{
				return;
			}

			IAnimatorController? controller = GetAnimatorController(animator);
			if (controller is null)
			{
				return;
			}

			foreach (IAnimationClip clip in controller.AnimationClipsP.WhereNotNull())
			{
				ExportAnimationClip(clip, bonePathToNode);
			}
		}
		catch (Exception ex)
		{
			Logger.Warning(LogCategory.Export, $"Failed to export animations: {ex.Message}");
		}
	}

	private static IAnimator? FindAnimatorInHierarchy(IGameObject gameObject)
	{
		IGameObject? current = gameObject;
		while (current is not null)
		{
			if (current.TryGetComponent(out IAnimator? animator))
			{
				return animator;
			}
			ITransform? transform = current.GetTransform();
			ITransform? parent = transform?.Father_C4P;
			current = parent?.GameObject_C4P;
		}
		return null;
	}

	private static IAnimatorController? GetAnimatorController(IAnimator animator)
	{
		// Multiple versioned controller properties exist — try each
		return animator.Controller_PPtr_AnimatorController_4P as IAnimatorController
			?? animator.Controller_PPtr_RuntimeAnimatorController_5P as IAnimatorController
			?? animator.Controller_PPtr_RuntimeAnimatorController_4_3P as IAnimatorController;
	}

	private static void ExportAnimationClip(IAnimationClip clip, Dictionary<string, NodeBuilder> bonePathToNode)
	{
		string animName = clip.Name ?? "Animation";

		// Position curves
		foreach (IVector3Curve curve in clip.PositionCurves_C74)
		{
			string path = curve.Path.String ?? "";
			if (TryFindBoneNode(path, bonePathToNode, out NodeBuilder? boneNode))
			{
				var translationCurve = boneNode.UseTranslation(animName);
				foreach (IKeyframe_Vector3f keyframe in curve.Curve.Curve)
				{
					translationCurve.SetPoint(keyframe.Time, new System.Numerics.Vector3(
						keyframe.Value.X, keyframe.Value.Y, -keyframe.Value.Z));
				}
			}
		}

		// Rotation curves (quaternion)
		foreach (IQuaternionCurve curve in clip.RotationCurves_C74)
		{
			string path = curve.Path.String ?? "";
			if (TryFindBoneNode(path, bonePathToNode, out NodeBuilder? boneNode))
			{
				var rotationCurve = boneNode.UseRotation(animName);
				foreach (IKeyframe_Quaternionf keyframe in curve.Curve.Curve)
				{
					rotationCurve.SetPoint(keyframe.Time, new System.Numerics.Quaternion(
						keyframe.Value.X, keyframe.Value.Y, -keyframe.Value.Z, -keyframe.Value.W));
				}
			}
		}

		// Euler rotation curves
		if (clip.Has_EulerCurves_C74())
		{
			foreach (IVector3Curve curve in clip.EulerCurves_C74)
			{
				string path = curve.Path.String ?? "";
				if (TryFindBoneNode(path, bonePathToNode, out NodeBuilder? boneNode))
				{
					var rotationCurve = boneNode.UseRotation(animName);
					foreach (IKeyframe_Vector3f keyframe in curve.Curve.Curve)
					{
						var euler = new System.Numerics.Vector3(
							keyframe.Value.X * (MathF.PI / 180f),
							keyframe.Value.Y * (MathF.PI / 180f),
							keyframe.Value.Z * (MathF.PI / 180f));
						var quat = System.Numerics.Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);
						rotationCurve.SetPoint(keyframe.Time, new System.Numerics.Quaternion(
							quat.X, quat.Y, -quat.Z, -quat.W));
					}
				}
			}
		}

		// Scale curves
		foreach (IVector3Curve curve in clip.ScaleCurves_C74)
		{
			string path = curve.Path.String ?? "";
			if (TryFindBoneNode(path, bonePathToNode, out NodeBuilder? boneNode))
			{
				var scaleCurve = boneNode.UseScale(animName);
				foreach (IKeyframe_Vector3f keyframe in curve.Curve.Curve)
				{
					scaleCurve.SetPoint(keyframe.Time, new System.Numerics.Vector3(
						keyframe.Value.X, keyframe.Value.Y, keyframe.Value.Z));
				}
			}
		}
	}

	private static bool TryFindBoneNode(string path, Dictionary<string, NodeBuilder> bonePathToNode, [NotNullWhen(true)] out NodeBuilder? boneNode)
	{
		// Try exact path
		if (bonePathToNode.TryGetValue(path, out boneNode))
		{
			return true;
		}

		// Try just the bone name (last component)
		int lastSlash = path.LastIndexOf('/');
		if (lastSlash >= 0)
		{
			string boneName = path[(lastSlash + 1)..];
			if (bonePathToNode.TryGetValue(boneName, out boneNode))
			{
				return true;
			}
		}

		boneNode = null;
		return false;
	}

	private static IGameObject GetRoot(IUnityObjectBase asset)
	{
		return asset switch
		{
			IGameObject gameObject => gameObject.GetRoot(),
			IComponent component => component.GameObject_C2P!.GetRoot(),
			_ => throw new InvalidOperationException()
		};
	}

	private static bool ReferencesDynamicMesh(IRenderer renderer)
	{
		return renderer.Has_StaticBatchInfo_C25() && renderer.StaticBatchInfo_C25.SubMeshCount == 0
			|| renderer.Has_SubsetIndices_C25() && renderer.SubsetIndices_C25.Count == 0;
	}

	private static int[] GetSubsetIndices(IRenderer renderer)
	{
		AccessListBase<IPPtr_Material> materials = renderer.Materials_C25;
		if (renderer.Has_SubsetIndices_C25())
		{
			return renderer.SubsetIndices_C25.Select(i => (int)i).ToArray();
		}
		else if (renderer.Has_StaticBatchInfo_C25())
		{
			return Enumerable.Range(renderer.StaticBatchInfo_C25.FirstSubMesh, renderer.StaticBatchInfo_C25.SubMeshCount).ToArray();
		}
		else
		{
			return Array.Empty<int>();
		}
	}

	private readonly record struct BuildParameters(
		MaterialBuilder DefaultMaterial,
		Dictionary<ITexture2D, MemoryImage> ImageCache,
		Dictionary<IMaterial, MaterialBuilder> MaterialCache,
		Dictionary<IMesh, MeshData> MeshCache,
		Dictionary<ITransform, NodeBuilder> BoneNodeCache,
		bool IsScene)
	{
		public BuildParameters(bool isScene) : this(new MaterialBuilder("DefaultMaterial"), new(), new(), new(), new(), isScene) { }
		public bool TryGetOrMakeMeshData(IMesh mesh, out MeshData meshData)
		{
			if (MeshCache.TryGetValue(mesh, out meshData))
			{
				return true;
			}
			else if (MeshData.TryMakeFromMesh(mesh, out meshData))
			{
				MeshCache.Add(mesh, meshData);
				return true;
			}
			return false;
		}

		public MaterialBuilder GetOrMakeMaterial(IMaterial? material)
		{
			if (material is null)
			{
				return DefaultMaterial;
			}
			if (!MaterialCache.TryGetValue(material, out MaterialBuilder? materialBuilder))
			{
				materialBuilder = MakeMaterialBuilder(material);
				MaterialCache.Add(material, materialBuilder);
			}
			return materialBuilder;
		}

		public bool TryGetOrMakeImage(ITexture2D texture, out MemoryImage image)
		{
			if (!ImageCache.TryGetValue(texture, out image))
			{
				if (TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
				{
					using MemoryStream memoryStream = new();
					bitmap.SaveAsPng(memoryStream);
					image = new MemoryImage(memoryStream.ToArray());
					ImageCache.Add(texture, image);
					return true;
				}
				return false;
			}
			else
			{
				return true;
			}
		}

		private MaterialBuilder MakeMaterialBuilder(IMaterial material)
		{
			MaterialBuilder materialBuilder = new MaterialBuilder(material.Name);
			GetTextures(material, out ITexture2D? mainTexture, out ITexture2D? normalTexture);
			if (mainTexture is not null && TryGetOrMakeImage(mainTexture, out MemoryImage mainImage))
			{
				materialBuilder.WithBaseColor(mainImage);
			}
			if (normalTexture is not null && TryGetOrMakeImage(normalTexture, out MemoryImage normalImage))
			{
				materialBuilder.WithNormal(normalImage);
			}
			return materialBuilder;
		}

		private static void GetTextures(IMaterial material, out ITexture2D? mainTexture, out ITexture2D? normalTexture)
		{
			mainTexture = null;
			normalTexture = null;
			ITexture2D? mainReplacement = null;
			foreach ((Utf8String utf8Name, IUnityTexEnv textureParameter) in material.GetTextureProperties())
			{
				string name = utf8Name.String;
				if (IsMainTexture(name))
				{
					mainTexture ??= textureParameter.Texture.TryGetAsset(material.Collection) as ITexture2D;
				}
				else if (IsNormalTexture(name))
				{
					normalTexture ??= textureParameter.Texture.TryGetAsset(material.Collection) as ITexture2D;
				}
				else
				{
					mainReplacement ??= textureParameter.Texture.TryGetAsset(material.Collection) as ITexture2D;
				}
			}
			mainTexture ??= mainReplacement;
		}

		private static bool IsMainTexture(string textureName)
		{
			return textureName is "_MainTex" or "_BaseMap" or "_BaseColor" or "_BaseColorMap" or "texture" or "Texture" or "_Texture";
		}

		private static bool IsNormalTexture(string textureName)
		{
			return textureName is "_Normal" or "_NormalMap" or "_BumpMap" or "Normal" or "normal";
		}
	}

	private readonly struct MaterialList
	{
		private readonly AccessListBase<IPPtr_Material> materials;
		private readonly AssetCollection file;

		private MaterialList(AccessListBase<IPPtr_Material> materials, AssetCollection file)
		{
			this.materials = materials;
			this.file = file;
		}

		public MaterialList(IRenderer renderer) : this(renderer.Materials_C25, renderer.Collection) { }

		public int Count => materials.Count;

		public IMaterial? this[int index]
		{
			get
			{
				if (index >= materials.Count)
				{
					return null;
				}
				return materials[index].TryGetAsset(file);
			}
		}
	}
}
