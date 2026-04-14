using AssetRipper.Assets;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.SourceGenerated.Classes.ClassID_114;
using AssetRipper.SourceGenerated.Extensions;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public static class TemplateClassifier
{
    public const string RuleVersion = "v2";
    public const string RuleDescription = "For MonoBehaviour assets, resolve m_Script and match script class names ending with 'Template' (Ordinal comparison).";

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

    public static TemplateClassificationMetadata GetMetadata() => new()
    {
        RuleVersion = RuleVersion,
        RuleDescription = RuleDescription,
    };
}
