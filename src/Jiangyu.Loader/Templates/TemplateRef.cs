namespace Jiangyu.Loader.Templates;

/// <summary>A template named by its canonical type name and id: what a held block waits on,
/// and what a clone's self-dependency exception is matched against.</summary>
internal readonly record struct TemplateRef(string Type, string Id)
{
    public override string ToString() => Type + ":" + Id;
}
