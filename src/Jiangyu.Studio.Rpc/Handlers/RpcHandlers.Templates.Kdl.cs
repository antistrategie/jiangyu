using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_templates_parse",
        "Parse a KDL template patch string and return the structured document or errors with line numbers. Use this to validate KDL you've written before saving to disk.")]
    [McpParam("text", "string", "KDL template patch source text to parse.", Required = true)]
    internal static JsonElement TemplatesParse(JsonElement? parameters)
    {
        var text = RequireString(parameters, "text");
        var document = KdlTemplateParser.ParseText(text);

        // Validate node/directive shapes against the template type catalogue
        // when available. Failures are non-fatal: if the catalog can't load
        // (no game configured, bad path), we just return parse errors only.
        var catalog = TryGetCachedCatalog();
        if (catalog != null)
        {
            // Editor-side validation needs the same BankId resolver and
            // tagged-discriminator allowlist that the compile path
            // installs, otherwise cross-references like
            // `Stem.ID.bankId` strings and `RoleGuid "Entity"` strings
            // fail with cryptic errors. Build the resolver locally and
            // pass it through; no process-static slot.
            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            IReadOnlyList<Jiangyu.Core.Models.AssetEntry>? indexedAssets = null;
            IBankIdResolver? bankIdResolver = null;
            if (resolution.Success)
            {
                EnsureTaggedDiscriminatorsInstalled(RpcHelpers.RequireEnvironment());
                var pipeline = RpcHelpers.RequireEnvironment().CreateAssetPipelineService(
                    NullProgressSink.Instance, NullLogSink.Instance);
                var assetIndex = pipeline.LoadIndex();
                if (assetIndex?.Assets is { } assets)
                {
                    indexedAssets = assets;
                    var bankPairs = new List<KeyValuePair<string, int>>();
                    foreach (var a in assets)
                    {
                        if (a.Name != null && a.SoundBank?.BankId is not null)
                            bankPairs.Add(new KeyValuePair<string, int>(a.Name, a.SoundBank.BankId.Value));
                    }
                    // Mod-defined SoundBank clones: surface their
                    // cloneId → FNV(cloneId) so cross-references resolve
                    // at editor-parse time. Mirrors CompilationService,
                    // which aggregates every templates/*.kdl in one pass.
                    // Editor-parse has to walk those files itself because
                    // the RPC sees only the document being edited; without
                    // the project scan, a squad_leader patch referencing a
                    // voicelines.kdl SoundBank clone gets a spurious
                    // "no SoundBank named …" error every keystroke.
                    var seenBankNames = new HashSet<string>(StringComparer.Ordinal);
                    void AddBankClone(string name)
                    {
                        if (string.IsNullOrEmpty(name)) return;
                        if (!seenBankNames.Add(name)) return;
                        bankPairs.Add(new KeyValuePair<string, int>(
                            name, HashableIdFieldRegistry.Fnv1a32(name)));
                    }
                    foreach (var node in document.Nodes)
                    {
                        if (node.Kind != KdlEditorNodeKind.Clone) continue;
                        if (!string.Equals(node.TemplateType, "SoundBank", StringComparison.Ordinal)) continue;
                        AddBankClone(node.CloneId ?? string.Empty);
                    }
                    foreach (var clone in EnumerateProjectClones())
                    {
                        if (!string.Equals(clone.TemplateType, "SoundBank", StringComparison.Ordinal)) continue;
                        AddBankClone(clone.Id);
                    }
                    bankIdResolver = new InMemoryBankIdResolver(bankPairs);
                }
            }

            TemplateCatalogValidator.ValidateEditorDocument(document, catalog, indexedAssets, bankIdResolver);
        }

        return JsonSerializer.SerializeToElement(document);
    }

    [McpTool("jiangyu_templates_serialise",
        "Serialise a parsed KDL template document back to KDL text. Returns {\"text\": \"...\"}. Useful for round-tripping after programmatic edits.")]
    [McpParam("document", "object", "The KdlEditorDocument object from jiangyu_templates_parse.", Required = true)]
    internal static JsonElement TemplatesSerialise(JsonElement? parameters)
    {
        if (parameters is not { } p)
            throw new ArgumentException("Missing parameters");

        var document = p.Deserialize<KdlEditorDocument>()
            ?? throw new ArgumentException("Could not deserialise editor document");

        // Strip composite=/handler=/ref= type attributes from values where
        // the destination field is monomorphic, so the emitted KDL relies
        // on inference. Studio displays the type from a parse-side fill-in,
        // but the on-disk text stays terse.
        var catalog = TryGetCachedCatalog();
        if (catalog != null)
            TemplateCatalogValidator.NormaliseForEmit(document, catalog);

        var text = KdlTemplateSerialiser.Serialise(document);
        return JsonSerializer.SerializeToElement(new { text });
    }
}
