using AsmResolver.DotNet;
using AssetRipper.Assets;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Classes.ClassID_115;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public static class TemplateClassifier
{
    public const string RuleVersion = "v3";
    public const string RuleDescription = "For MonoBehaviour assets, resolve m_Script and match script class names ending with 'Template' (Ordinal). "
        + "Fallback: walk managed inheritance chain for a Template-named ancestor.";

    public static bool IsTemplateLike(string? className) =>
        !string.IsNullOrWhiteSpace(className)
        && className.EndsWith("Template", StringComparison.Ordinal);

    public static bool TryGetTemplateClassName(IUnityObjectBase asset, out string? className)
    {
        className = null;

        if (asset is not IMonoBehaviour monoBehaviour)
        {
            return false;
        }

        if (!monoBehaviour.TryGetScript(out var script))
        {
            return false;
        }

        string scriptClassName = script.ClassName_R.String;
        if (!IsTemplateLike(scriptClassName))
        {
            return false;
        }

        className = scriptClassName;
        return true;
    }

    /// <summary>
    /// Inheritance-aware fallback for non-Template-named MonoBehaviours.
    /// Walks the managed type hierarchy looking for a Template-named ancestor.
    /// Returns the concrete class name (not the ancestor).
    /// </summary>
    /// <remarks>
    /// JIANGYU-CONTRACT: assumes the *Template naming convention in MENACE is exclusive to
    /// data-template ScriptableObjects. If a non-template MonoBehaviour ever inherits from a
    /// Template-named base, this would produce a false positive. Validated against full
    /// resources.assets survey (13,448 MonoBehaviours, zero false positives observed).
    /// Scope: current MENACE game data as of 2026-04-16.
    /// </remarks>
    public static bool TryGetInheritedTemplateClassName(
        IMonoBehaviour monoBehaviour,
        IAssemblyManager assemblyManager,
        out string? className,
        out string? ancestorClassName)
    {
        className = null;
        ancestorClassName = null;

        if (!monoBehaviour.TryGetScript(out IMonoScript? script))
        {
            return false;
        }

        string scriptClassName = script.ClassName_R.String;

        // Skip types that already match the suffix rule — those are handled by the fast path.
        if (IsTemplateLike(scriptClassName))
        {
            return false;
        }

        TypeDefinition typeDefinition;
        try
        {
            typeDefinition = script.GetTypeDefinition(assemblyManager);
        }
        catch
        {
            return false;
        }

        string? ancestor = FindTemplateAncestor(typeDefinition);
        if (ancestor is null)
        {
            return false;
        }

        className = scriptClassName;
        ancestorClassName = ancestor;
        return true;
    }

    /// <summary>
    /// Walk the base type chain (skipping the type itself) looking for a class name
    /// ending with "Template". Returns the first matching ancestor name, or null.
    /// </summary>
    internal static string? FindTemplateAncestor(TypeDefinition typeDefinition)
    {
        // Start from the parent, not the type itself.
        TypeDefinition? current;
        try
        {
            current = typeDefinition.BaseType?.Resolve();
        }
        catch
        {
            return null;
        }

        // Guard against pathological inheritance chains.
        const int maxDepth = 20;
        int depth = 0;

        while (current is not null && depth < maxDepth)
        {
            string? name = current.Name?.ToString();
            if (IsTemplateLike(name))
            {
                return name;
            }

            depth++;
            try
            {
                current = current.BaseType?.Resolve();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static TemplateClassificationMetadata GetMetadata() => new()
    {
        RuleVersion = RuleVersion,
        RuleDescription = RuleDescription,
    };
}
