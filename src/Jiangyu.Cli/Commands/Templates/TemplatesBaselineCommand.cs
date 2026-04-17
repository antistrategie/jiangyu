using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesBaselineCommand
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private const string DefaultBaselineRelativePath = "validation/template-structure-baseline.json";

    public static Command Create()
    {
        var command = new Command("baseline", "Structural baseline generation and diff")
        {
            CreateGenerateCommand(),
            CreateDiffCommand(),
            CreateAuditCommand(),
        };

        return command;
    }

    private static Command CreateAuditCommand()
    {
        var sourcesOption = new Option<string>("--sources")
        {
            Description = "Path to baseline sources file",
            DefaultValueFactory = _ => "validation/template-structure-baseline.sources.json",
        };
        var committedOption = new Option<string>("--committed")
        {
            Description = "Path to the committed baseline JSON to audit against",
            DefaultValueFactory = _ => DefaultBaselineRelativePath,
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit the raw diff JSON instead of the human-readable report",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("audit", "Regenerate a fresh baseline from live game data, diff against the committed baseline, and flag types for revalidation")
        {
            sourcesOption,
            committedOption,
            jsonOption,
        };

        command.SetAction(parseResult =>
        {
            string sourcesPath = parseResult.GetValue(sourcesOption)!;
            string committedPath = parseResult.GetValue(committedOption)!;
            bool json = parseResult.GetValue(jsonOption);

            if (!Path.IsPathRooted(sourcesPath))
            {
                sourcesPath = Path.GetFullPath(sourcesPath);
            }

            if (!Path.IsPathRooted(committedPath))
            {
                committedPath = Path.GetFullPath(committedPath);
            }

            BaselineSources? sources = StructuralBaselineService.LoadSources(sourcesPath);
            if (sources is null)
            {
                Console.Error.WriteLine($"Error: sources file not found: {sourcesPath}");
                return 1;
            }

            StructuralBaseline? committed = StructuralBaselineService.LoadBaseline(committedPath);
            if (committed is null)
            {
                Console.Error.WriteLine($"Error: committed baseline not found: {committedPath}");
                return 1;
            }

            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var context = resolution.Context!;
            var service = context.CreateStructuralBaselineService(new ConsoleProgressSink(), new ConsoleLogSink());

            StructuralBaseline fresh;
            try
            {
                fresh = service.GenerateBaseline(sources);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: baseline regeneration failed: {ex.Message}");
                return 1;
            }

            BaselineDiff diff = StructuralBaselineService.DiffBaselines(committed, fresh);

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(diff, PrettyJsonOptions));
            }
            else
            {
                Console.WriteLine(StructuralBaselineService.FormatAuditReport(diff));
            }

            bool hasDrift = diff.AddedTypes.Count > 0
                || diff.RemovedTypes.Count > 0
                || diff.ChangedTypes.Count > 0;

            return hasDrift ? 1 : 0;
        });

        return command;
    }

    private static Command CreateGenerateCommand()
    {
        var sourcesOption = new Option<string>("--sources")
        {
            Description = "Path to baseline sources file",
            DefaultValueFactory = _ => "validation/template-structure-baseline.sources.json",
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "Path to write the baseline JSON",
            DefaultValueFactory = _ => DefaultBaselineRelativePath,
        };

        var command = new Command("generate", "Generate a structural baseline from curated sources")
        {
            sourcesOption,
            outputOption,
        };

        command.SetAction(parseResult =>
        {
            string sourcesPath = parseResult.GetValue(sourcesOption)!;
            string outputPath = parseResult.GetValue(outputOption)!;

            if (!Path.IsPathRooted(sourcesPath))
            {
                sourcesPath = Path.GetFullPath(sourcesPath);
            }

            if (!Path.IsPathRooted(outputPath))
            {
                outputPath = Path.GetFullPath(outputPath);
            }

            BaselineSources? sources = StructuralBaselineService.LoadSources(sourcesPath);
            if (sources is null)
            {
                Console.Error.WriteLine($"Error: sources file not found: {sourcesPath}");
                return 1;
            }

            var resolution = EnvironmentContext.ResolveFromGlobalConfig();
            if (!resolution.Success)
            {
                Console.Error.WriteLine(resolution.Error);
                return 1;
            }

            var context = resolution.Context!;
            var service = context.CreateStructuralBaselineService(new ConsoleProgressSink(), new ConsoleLogSink());

            try
            {
                StructuralBaseline baseline = service.GenerateBaseline(sources);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                string json = JsonSerializer.Serialize(baseline, PrettyJsonOptions);
                File.WriteAllText(outputPath, json + Environment.NewLine);

                Console.WriteLine($"Baseline written to: {outputPath}");
                Console.WriteLine($"  Types: {baseline.Types.Count}");
                Console.WriteLine($"  Generated at: {baseline.GeneratedAt:O}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: baseline generation failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static Command CreateDiffCommand()
    {
        var previousOption = new Option<string?>("--previous")
        {
            Description = "Path to the previous baseline JSON",
        };
        var currentOption = new Option<string>("--current")
        {
            Description = "Path to the current baseline JSON",
            DefaultValueFactory = _ => DefaultBaselineRelativePath,
        };

        var command = new Command("diff", "Compare two structural baselines for drift")
        {
            previousOption,
            currentOption,
        };

        command.SetAction(parseResult =>
        {
            string? previousPath = parseResult.GetValue(previousOption);
            string currentPath = parseResult.GetValue(currentOption)!;

            if (string.IsNullOrWhiteSpace(previousPath))
            {
                Console.Error.WriteLine("Error: --previous is required.");
                return 1;
            }

            if (!Path.IsPathRooted(previousPath))
            {
                previousPath = Path.GetFullPath(previousPath);
            }

            if (!Path.IsPathRooted(currentPath))
            {
                currentPath = Path.GetFullPath(currentPath);
            }

            StructuralBaseline? previous = StructuralBaselineService.LoadBaseline(previousPath);
            if (previous is null)
            {
                Console.Error.WriteLine($"Error: previous baseline not found: {previousPath}");
                return 1;
            }

            StructuralBaseline? current = StructuralBaselineService.LoadBaseline(currentPath);
            if (current is null)
            {
                Console.Error.WriteLine($"Error: current baseline not found: {currentPath}");
                return 1;
            }

            string defaultCurrentPath = Path.GetFullPath(DefaultBaselineRelativePath);
            if (string.Equals(currentPath, defaultCurrentPath, StringComparison.Ordinal))
            {
                var resolution = EnvironmentContext.ResolveFromGlobalConfig();
                if (!resolution.Success)
                {
                    Console.Error.WriteLine(resolution.Error);
                    return 1;
                }

                var context = resolution.Context!;
                var service = context.CreateStructuralBaselineService(new ConsoleProgressSink(), new ConsoleLogSink());
                CachedIndexStatus baselineStatus = service.GetBaselineStatus(current);
                if (!baselineStatus.IsCurrent)
                {
                    Console.Error.WriteLine($"Error: {baselineStatus.Reason}");
                    return 1;
                }
            }

            BaselineDiff diff = StructuralBaselineService.DiffBaselines(previous, current);

            string json = JsonSerializer.Serialize(diff, PrettyJsonOptions);
            Console.WriteLine(json);

            bool hasDrift = diff.AddedTypes.Count > 0
                || diff.RemovedTypes.Count > 0
                || diff.ChangedTypes.Count > 0;

            return hasDrift ? 1 : 0;
        });

        return command;
    }
}
