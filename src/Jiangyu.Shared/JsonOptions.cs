using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiangyu.Shared;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances used across the
/// project for config files, manifests, and cache payloads. Centralised so
/// every config/manifest reads/writes through identical formatting and a
/// future shape change (e.g. naming-policy adjustment) ripples in one place.
///
/// <para>Three families are exposed:
/// <list type="bullet">
///   <item><see cref="PrettyCamel"/> — indented, camelCase, skip null. The
///     dominant config/manifest format (jiangyu.json, project config, global
///     config, studio settings, agent sessions store).</item>
///   <item><see cref="PrettyRelaxedEscape"/> — same plus
///     <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>. Use when
///     the payload may contain Unicode or characters the strict encoder
///     would over-escape (e.g. authored mod descriptions with apostrophes).</item>
///   <item><see cref="PrettyPascalIgnoreNull"/> — indented, ignore null, but
///     no naming policy override. Used by cache payloads that keep PascalCase
///     JSON property names (asset index, structural baseline, IL2CPP
///     metadata supplement).</item>
///   <item><see cref="CompactCamel"/> — camelCase, not indented. The net wire
///     format (control messages and command payloads), kept compact for
///     bandwidth and readable in desync forensics.</item>
/// </list></para>
/// </summary>
public static class JsonOptions
{
    public static readonly JsonSerializerOptions CompactCamel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static readonly JsonSerializerOptions PrettyCamel = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions PrettyRelaxedEscape = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions PrettyPascalIgnoreNull = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
