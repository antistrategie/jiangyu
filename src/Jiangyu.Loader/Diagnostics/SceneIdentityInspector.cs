using System.Text.Json;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Opt-in dump of live scene identity: SpriteRenderer/Image/SpriteAsset/
/// TextureAsset/SkinnedMeshRenderer/AudioSource/AudioClip enumerated from
/// the active scene plus <c>Resources.FindObjectsOfTypeAll</c>. Used to
/// answer "what's actually live right now?" when a replacement or template
/// patch isn't landing as expected.
///
/// <para>Gated by <see cref="InspectionSink.IsEnabled"/>. Atlas-packed
/// sprites and off-scene-cached assets are visible even though the main
/// replacement sweeps can't see the latter.</para>
/// </summary>
internal static class SceneIdentityInspector
{
    public static void Dump(string sceneName, int buildIndex, MelonLogger.Instance log)
    {
        var flag = InspectionSink.GetCachedFlag();
        if (!flag.Enabled)
            return;

        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss");
            var safeSceneName = InspectionSink.SanitiseForFileName(sceneName);
            var filePath = Path.Combine(InspectionSink.GetOutputDirectory(), $"{timestamp}-{safeSceneName}.json");

            var dump = BuildDump(sceneName, buildIndex);
            File.WriteAllText(filePath, JsonSerializer.Serialize(dump, InspectionSink.JsonOptions));

            log.Msg(
                $"[inspect] Wrote runtime dump: {filePath}  " +
                $"(sprite renderers={dump.SpriteRenderers.Count}  " +
                $"ui images={dump.UiImages.Count}  " +
                $"sprite assets={dump.SpriteAssets.Count}  " +
                $"audio sources={dump.AudioSources.Count}  " +
                $"audio clips={dump.AudioClipAssets.Count})");

            InspectionSink.EnforceRetention(flag.RetentionCap, log);
        }
        catch (Exception ex)
        {
            log.Error($"[inspect] dump failed: {ex}");
        }
    }

    private static RuntimeDump BuildDump(string sceneName, int buildIndex)
    {
        var dump = new RuntimeDump
        {
            Timestamp = DateTimeOffset.UtcNow,
            SceneName = sceneName,
            SceneBuildIndex = buildIndex,
        };

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpriteRenderer>(), true))
        {
            var renderer = obj.Cast<SpriteRenderer>();
            dump.SpriteRenderers.Add(new SpriteRendererInfo
            {
                GameObjectPath = GameObjectPath(renderer?.gameObject),
                SpriteName = renderer?.sprite?.name,
                Enabled = renderer?.enabled ?? false,
            });
        }

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Image>(), true))
        {
            var image = obj.Cast<Image>();
            dump.UiImages.Add(new UiImageInfo
            {
                GameObjectPath = GameObjectPath(image?.gameObject),
                SpriteName = image?.sprite?.name,
                Enabled = image?.enabled ?? false,
            });
        }

        // Diagnostic counts for why UiImages / SpriteRenderers may come back empty.
        // If Canvas > 0 but Graphic == 0, UI roots exist but Graphic components
        // haven't awoken at scene-load (timing). If Canvas == 0, UI isn't loaded
        // into the scene yet. If Graphic > 0 but Image == 0, Image type resolution
        // is broken.
        dump.Counts.Canvases = CountOfType(Il2CppType.Of<Canvas>());
        dump.Counts.Graphics = CountOfType(Il2CppType.Of<Graphic>());
        dump.Counts.RawImages = CountOfType(Il2CppType.Of<RawImage>());
        dump.Counts.UiDocuments = CountOfType(Il2CppType.Of<UnityEngine.UIElements.UIDocument>());
        dump.Counts.TotalGameObjects = CountOfType(Il2CppType.Of<GameObject>());

        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
        {
            var sprite = obj.Cast<Sprite>();
            if (sprite == null)
                continue;
            dump.SpriteAssets.Add(new SpriteAssetInfo
            {
                Name = sprite.name,
                TextureName = sprite.texture?.name,
            });
        }

        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>()))
        {
            var texture = obj?.TryCast<Texture2D>();
            if (texture == null)
                continue;
            dump.TextureAssets.Add(new TextureAssetInfo
            {
                Name = texture.name,
                Width = texture.width,
                Height = texture.height,
                Format = texture.format.ToString(),
                HideFlags = texture.hideFlags.ToString(),
            });
        }

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true))
        {
            var smr = obj?.TryCast<SkinnedMeshRenderer>();
            if (smr == null)
                continue;
            dump.SkinnedMeshRenderers.Add(new SkinnedMeshRendererInfo
            {
                GameObjectPath = GameObjectPath(smr.gameObject),
                MeshName = smr.sharedMesh?.name,
                BoneCount = smr.bones?.Length ?? 0,
                RootBoneName = smr.rootBone?.name,
                HideFlags = smr.hideFlags.ToString(),
                SceneLoaded = smr.gameObject != null && smr.gameObject.scene.isLoaded,
            });
        }

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<AudioSource>(), true))
        {
            var source = obj.Cast<AudioSource>();
            dump.AudioSources.Add(new AudioSourceInfo
            {
                GameObjectPath = GameObjectPath(source?.gameObject),
                ClipName = source?.clip?.name,
                PlayOnAwake = source?.playOnAwake ?? false,
            });
        }

        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<AudioClip>()))
        {
            var clip = obj.Cast<AudioClip>();
            if (clip == null)
                continue;
            dump.AudioClipAssets.Add(new AudioClipAssetInfo
            {
                Name = clip.name,
                LengthSeconds = clip.length,
            });
        }

        return dump;
    }

    private static int CountOfType(Il2CppSystem.Type type)
    {
        var count = 0;
        foreach (var _ in UnityEngine.Object.FindObjectsOfType(type, true))
            count++;
        return count;
    }

    private static string GameObjectPath(GameObject gameObject)
    {
        if (gameObject == null)
            return null;

        var parts = new List<string>();
        var transform = gameObject.transform;
        while (transform != null)
        {
            parts.Add(transform.name);
            transform = transform.parent;
        }
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    private sealed class RuntimeDump
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SceneName { get; set; }
        public int SceneBuildIndex { get; set; }
        public DiagnosticCounts Counts { get; } = new();
        public List<SpriteRendererInfo> SpriteRenderers { get; } = new();
        public List<UiImageInfo> UiImages { get; } = new();
        public List<SpriteAssetInfo> SpriteAssets { get; } = new();
        public List<TextureAssetInfo> TextureAssets { get; } = new();
        public List<SkinnedMeshRendererInfo> SkinnedMeshRenderers { get; } = new();
        public List<AudioSourceInfo> AudioSources { get; } = new();
        public List<AudioClipAssetInfo> AudioClipAssets { get; } = new();
    }

    private sealed class SkinnedMeshRendererInfo
    {
        public string GameObjectPath { get; set; }
        public string MeshName { get; set; }
        public int BoneCount { get; set; }
        public string RootBoneName { get; set; }
        public string HideFlags { get; set; }
        public bool SceneLoaded { get; set; }
    }

    private sealed class TextureAssetInfo
    {
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Format { get; set; }
        public string HideFlags { get; set; }
    }

    private sealed class DiagnosticCounts
    {
        public int Canvases { get; set; }
        public int Graphics { get; set; }
        public int RawImages { get; set; }
        public int UiDocuments { get; set; }
        public int TotalGameObjects { get; set; }
    }

    private sealed class SpriteRendererInfo
    {
        public string GameObjectPath { get; set; }
        public string SpriteName { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class UiImageInfo
    {
        public string GameObjectPath { get; set; }
        public string SpriteName { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class SpriteAssetInfo
    {
        public string Name { get; set; }
        public string TextureName { get; set; }
    }

    private sealed class AudioSourceInfo
    {
        public string GameObjectPath { get; set; }
        public string ClipName { get; set; }
        public bool PlayOnAwake { get; set; }
    }

    private sealed class AudioClipAssetInfo
    {
        public string Name { get; set; }
        public float LengthSeconds { get; set; }
    }
}
