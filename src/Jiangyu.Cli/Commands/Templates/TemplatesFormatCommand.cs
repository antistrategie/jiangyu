using System.CommandLine;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Templates.Kdl;

namespace Jiangyu.Cli.Commands.Templates;

/// <summary>
/// Runs the editor pipeline (parse → validate → normalise → serialise) over
/// every <c>*.kdl</c> under <c>templates/</c>. Output matches what the visual
/// editor produces on a save, so a modder can apply the same canonicalisation
/// (composite-type stripping, RoleGuid name → int resolution, enum / asset
/// shorthand coercion) in bulk from the command line.
/// </summary>
public static class TemplatesFormatCommand
{
    private const string DefaultAssemblyRelativePath = "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll";
    private const string MelonLoaderNet6RelativePath = "MelonLoader/net6";

    public static Command Create()
    {
        var pathArg = new Argument<string?>("path")
        {
            Description = "File or directory to format. Defaults to ./templates/.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var checkOption = new Option<bool>("--check")
        {
            Description = "Exit 1 if any file would change. No files are written.",
        };

        var command = new Command(
            "format",
            "Canonicalise KDL template files via the editor parse → validate → normalise → serialise pipeline.")
        {
            pathArg,
            checkOption,
        };

        command.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathArg);
            var check = parseResult.GetValue(checkOption);
            return Run(path, check);
        });

        return command;
    }

    private static int Run(string? path, bool check)
    {
        var inputPath = path ?? Path.Combine(Directory.GetCurrentDirectory(), "templates");
        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine($"Error: no such file or directory: {inputPath}");
            return 1;
        }

        var files = File.Exists(inputPath)
            ? [inputPath]
            : Directory.EnumerateFiles(inputPath, "*.kdl", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

        if (files.Count == 0)
        {
            Console.WriteLine($"No .kdl files found under {inputPath}.");
            return 0;
        }

        // Set up the dependencies the validator wants: catalog, asset index,
        // tagged-discriminator allowlist, and a bankId resolver covering both
        // shipped SoundBanks and any SoundBank clones authored across the
        // project's KDL files. The visual editor builds this on every parse
        // RPC; we build it once and reuse across files.
        using var catalog = TryLoadCatalog();
        IReadOnlyList<AssetEntry>? indexedAssets = null;
        IBankIdResolver? bankIdResolver = null;
        if (catalog != null)
        {
            (indexedAssets, bankIdResolver) = BuildAssetContext(files);
        }

        var changed = new List<string>();
        var errors = 0;

        foreach (var file in files)
        {
            string original;
            try
            {
                original = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error reading {file}: {ex.Message}");
                errors++;
                continue;
            }

            string formatted;
            try
            {
                formatted = FormatDocument(original, catalog, indexedAssets, bankIdResolver);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error formatting {file}: {ex.Message}");
                errors++;
                continue;
            }

            if (string.Equals(original, formatted, StringComparison.Ordinal)) continue;

            changed.Add(file);
            if (check)
            {
                Console.WriteLine($"would change: {file}");
            }
            else
            {
                try
                {
                    File.WriteAllText(file, formatted);
                    Console.WriteLine($"formatted: {file}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error writing {file}: {ex.Message}");
                    errors++;
                }
            }
        }

        if (errors > 0) return 1;
        if (check && changed.Count > 0) return 1;

        var summary = check
            ? $"{changed.Count} of {files.Count} file(s) would change."
            : $"{changed.Count} of {files.Count} file(s) reformatted.";
        Console.WriteLine(summary);
        return 0;
    }

    private static string FormatDocument(
        string text,
        TemplateTypeCatalog? catalog,
        IReadOnlyList<AssetEntry>? indexedAssets,
        IBankIdResolver? bankIdResolver)
    {
        // KdlSharp discards /- blocks at parse time, so strip them before the
        // parse pipeline runs and reinject them into the formatted output.
        var slashdashBlocks = KdlSlashdashPreserver.Extract(text, out var stripped);
        var doc = KdlTemplateParser.ParseText(stripped);
        if (catalog != null)
        {
            TemplateCatalogValidator.ValidateEditorDocument(doc, catalog, indexedAssets, bankIdResolver);
            TemplateCatalogValidator.NormaliseForEmit(doc, catalog, bankIdResolver);
        }
        var formatted = KdlTemplateSerialiser.Serialise(doc);
        return KdlSlashdashPreserver.Reinject(formatted, slashdashBlocks);
    }

    /// <summary>
    /// Load the type catalog from the configured game install. Returns null
    /// when no game is configured or the assembly is missing — callers fall
    /// back to a cosmetic-only round-trip (parse + serialise, no validator
    /// rewrites).
    /// </summary>
    private static TemplateTypeCatalog? TryLoadCatalog()
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success) return null;

        var gamePath = Path.GetDirectoryName(resolution.Context!.GameDataPath);
        if (string.IsNullOrEmpty(gamePath)) return null;

        var assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        if (!File.Exists(assemblyPath)) return null;

        var additionalSearchDirectories = new List<string>();
        var melonLoaderNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
        if (Directory.Exists(melonLoaderNet6))
            additionalSearchDirectories.Add(melonLoaderNet6);

        try
        {
            var supplement = Il2CppMetadataCache.LoadIfPresent(resolution.Context.CachePath);
            return TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirectories, supplement);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Build the per-run resolver context: the asset list (used by the
    /// validator for Conversation role resolution) and a bankId resolver
    /// covering shipped SoundBanks plus any SoundBank clones the modder has
    /// declared across the project's KDL files. Also installs the
    /// tagged-discriminator allowlist so tagged-string composites
    /// (ConversationNode, etc.) validate.
    /// </summary>
    private static (IReadOnlyList<AssetEntry>? Assets, IBankIdResolver? Resolver) BuildAssetContext(
        IReadOnlyList<string> files)
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success) return (null, null);

        var pipeline = resolution.Context!.CreateAssetPipelineService(
            NullProgressSink.Instance, NullLogSink.Instance);
        var assetIndex = pipeline.LoadIndex();
        if (assetIndex?.Assets is not { } assets) return (null, null);

        TaggedDiscriminatorIndex.Install(assetIndex.TaggedDiscriminators);

        var bankPairs = new List<KeyValuePair<string, int>>();
        foreach (var a in assets)
        {
            if (a.Name != null && a.SoundBank?.BankId is not null)
                bankPairs.Add(new KeyValuePair<string, int>(a.Name, a.SoundBank.BankId.Value));
        }

        // Walk every file in the format run for SoundBank clones. The
        // compiler does the same scan during build; mirroring it here means
        // a `set "bankId" "<my_clone_bank>"` resolves without a stale
        // "no SoundBank named …" failure that would skip the file's
        // normalisation pass.
        var seenBankNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var doc = KdlTemplateParser.ParseText(text);
            foreach (var node in doc.Nodes)
            {
                if (node.Kind != KdlEditorNodeKind.Clone) continue;
                if (!string.Equals(node.TemplateType, "SoundBank", StringComparison.Ordinal)) continue;
                var name = node.CloneId ?? string.Empty;
                if (string.IsNullOrEmpty(name) || !seenBankNames.Add(name)) continue;
                bankPairs.Add(new KeyValuePair<string, int>(
                    name, HashableIdFieldRegistry.Fnv1a32(name)));
            }
        }

        return (assets, new InMemoryBankIdResolver(bankPairs));
    }
}
