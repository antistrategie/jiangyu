namespace Jiangyu.Studio.Host.Acp;

/// <summary>
/// One-shot project / toolkit context the host pushes to every new agent
/// session as a synthetic first prompt. Single source of truth for both
/// the firing path (<c>HandleAgentSessionCreate</c> → <see cref="Blurb"/>)
/// and the replay-skip path (<see cref="AcpClientHandler"/> matches on
/// <see cref="Sentinel"/> to suppress historical synthetic turns when a
/// session is reloaded; the live path uses
/// <c>AcpClientHandler.SuppressUpdates</c> instead).
/// </summary>
internal static class AgentContext
{
    /// <summary>
    /// Stable opening line. Agents replay user messages verbatim on session
    /// load, so a content-prefix match on this line reliably identifies
    /// the synthetic turn in history. No real user would type this.
    /// </summary>
    public const string Sentinel = "[project context — no reply needed, this is a one-shot brief from the host]";

    /// <summary>
    /// Full priming text. Phrased as a brief; "no reply needed" + "wait
    /// for the user's first real prompt" so the agent doesn't burn a turn
    /// acknowledging.
    /// </summary>
    public const string Blurb = $$"""
        {{Sentinel}}

        You are working in a Jiangyu mod project. Jiangyu is a modkit for MENACE (Unity 6, IL2CPP). The working directory holds replacement assets and KDL template patches that compile into an AssetBundle plus a JSON loader manifest.

        Project layout:
        - jiangyu.json — mod manifest (id, name, dependencies)
        - templates/*.kdl — typed patches over MENACE's data templates (units, weapons, perks). Operations include `set "Field" value`, `clone "Source" id="new"`, etc.
        - assets/ — replacement files (textures, sprites, models, audio) matched to game assets by path + filename.
        - compiled/ — build output produced by the jiangyu_compile tool.

        Workflows:
        - Modify template values: jiangyu_templates_search → jiangyu_templates_query (returns enumMembers and namedArrayEnumTypeName inline; do not guess enum types) → write/edit a KDL patch in templates/.
        - Inspect current vanilla values: jiangyu_templates_inspect or jiangyu_templates_value (NamedArray elements carry their paired enum-member name on each slot).
        - Build: jiangyu_compile.

        Reference docs (manifest schema, KDL operations, replacement formats, troubleshooting) live in jiangyu_docs_list / jiangyu_docs_read tools — call those when you need authoritative material. (They're also exposed as MCP resources at jiangyu://docs/... for clients that prefer that surface.)

        Wait for the user's first real prompt before responding.
        """;
}
