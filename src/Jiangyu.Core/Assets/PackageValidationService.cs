using System.Text.Json.Nodes;

namespace Jiangyu.Core.Assets;

public static class PackageValidationService
{
    public sealed class ValidationResult
    {
        public required List<string> Issues { get; init; }
        public required List<string> Info { get; init; }
        public bool IsValid => Issues.Count == 0;
    }

    public static ValidationResult Validate(string packageDir)
    {
        var issues = new List<string>();
        var info = new List<string>();

        // Check for model file
        var gltfPath = Path.Combine(packageDir, "model.gltf");
        var glbPath = Path.Combine(packageDir, "model.glb");
        bool isClean = File.Exists(gltfPath);
        bool isRaw = File.Exists(glbPath);

        if (isClean)
        {
            info.Add("Model: model.gltf (clean export)");
            ValidateGltf(gltfPath, packageDir, issues, info);
        }
        else if (isRaw)
        {
            info.Add("Model: model.glb (raw export)");
        }
        else
        {
            issues.Add("No model file found (expected model.gltf or model.glb)");
        }

        // Check textures directory
        var texturesDir = Path.Combine(packageDir, "textures");
        if (Directory.Exists(texturesDir))
        {
            var texFiles = Directory.GetFiles(texturesDir, "*.png");
            info.Add($"Textures: {texFiles.Length} PNG files");
            foreach (var tex in texFiles)
            {
                info.Add($"  {Path.GetFileName(tex)}");
            }
        }
        else if (isClean)
        {
            issues.Add("No textures/ directory (expected for clean export)");
        }

        // Check for stale sidecar
        var sidecarPath = Path.Combine(packageDir, "jiangyu.export.json");
        if (File.Exists(sidecarPath))
        {
            issues.Add("Stale jiangyu.export.json found — this format has been replaced by .gltf material graph");
        }

        return new ValidationResult { Issues = issues, Info = info };
    }

    private static void ValidateGltf(string gltfPath, string packageDir, List<string> issues, List<string> info)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(gltfPath));
        }
        catch (Exception ex)
        {
            issues.Add($"Failed to parse model.gltf: {ex.Message}");
            return;
        }

        if (root is not JsonObject gltf)
        {
            issues.Add("model.gltf root is not a JSON object");
            return;
        }

        // Check cleaned flag
        var cleaned = gltf["extras"]?["jiangyu"]?["cleaned"]?.GetValue<bool>();
        if (cleaned == true)
        {
            info.Add("Cleaned: yes (1x metre scale)");
        }
        else
        {
            issues.Add("Missing extras.jiangyu.cleaned flag");
        }

        // Check materials
        var materials = gltf["materials"]?.AsArray();
        if (materials is null || materials.Count == 0)
        {
            issues.Add("No materials in model.gltf");
            return;
        }

        info.Add($"Materials: {materials.Count}");

        foreach (var mat in materials)
        {
            if (mat is not JsonObject matObj) continue;
            var matName = matObj["name"]?.GetValue<string>() ?? "(unnamed)";

            var channels = new List<string>();
            var pbr = matObj["pbrMetallicRoughness"];
            if (pbr?["baseColorTexture"] is not null) channels.Add("baseColor");
            if (pbr?["metallicRoughnessTexture"] is not null) channels.Add("metallicRoughness");
            if (matObj["normalTexture"] is not null) channels.Add("normal");
            if (matObj["emissiveTexture"] is not null) channels.Add("emissive");
            if (matObj["occlusionTexture"] is not null) channels.Add("occlusion");

            var nonStandard = matObj["extras"]?["jiangyu"]?["textures"] as JsonObject;
            var nsCount = nonStandard?.Count ?? 0;

            info.Add($"  {matName}: {string.Join(", ", channels)}{(nsCount > 0 ? $" + {nsCount} non-standard" : "")}");

            // Validate non-standard texture files exist
            if (nonStandard is not null)
            {
                foreach (var (prop, pathNode) in nonStandard)
                {
                    var relPath = pathNode?.GetValue<string>();
                    if (string.IsNullOrEmpty(relPath)) continue;
                    var absPath = Path.Combine(packageDir, relPath);
                    if (!File.Exists(absPath))
                    {
                        issues.Add($"Material '{matName}': non-standard texture '{prop}' references missing file: {relPath}");
                    }
                }
            }
        }

        // Check images have names
        var images = gltf["images"]?.AsArray();
        if (images is not null)
        {
            foreach (var img in images)
            {
                if (img is not JsonObject imgObj) continue;
                var name = imgObj["name"]?.GetValue<string>();
                var uri = imgObj["uri"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    issues.Add($"Image with uri '{uri}' has no name (compiler needs Image.Name for texture prefix inference)");
                }
                if (!string.IsNullOrEmpty(uri))
                {
                    var absPath = Path.Combine(packageDir, uri);
                    if (!File.Exists(absPath))
                    {
                        issues.Add($"Image uri references missing file: {uri}");
                    }
                }
            }
        }
    }
}
