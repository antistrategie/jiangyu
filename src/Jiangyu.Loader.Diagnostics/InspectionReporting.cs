using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppMenace.Tactical;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>The status strings a diagnostic check reports.</summary>
internal static class DiagnosticStatus
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Error = "error";
    public const string Skipped = "skipped";
}

/// <summary>One named check in a diagnostic report, with free-form evidence.</summary>
internal sealed class InspectionCheck
{
    public string Name { get; set; }
    public string Status { get; set; }
    public string Detail { get; set; }
    public Dictionary<string, object> Evidence { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A timestamped diagnostic report. <see cref="Live"/> is nullable so a report that
/// has no live/structural distinction (the verb probe) leaves it unset and the
/// null-ignoring serializer omits it, while the injection gate sets it.
/// </summary>
internal sealed class InspectionReport
{
    public DateTimeOffset Timestamp { get; set; }
    public string SceneTag { get; set; }
    public bool? Live { get; set; }
    public string SdkLoaderVersion { get; set; }
    public string GameVersion { get; set; }
    public List<InspectionCheck> Checks { get; } = new();
}

/// <summary>
/// Shared report plumbing for the loader's re-runnable diagnostics (the injection
/// gate and the verb-surface probe). Each diagnostic supplies a <c>label</c> that
/// tags its log lines and report filename (<c>&lt;timestamp&gt;-&lt;label&gt;-&lt;tag&gt;.json</c>);
/// the build/serialise/log mechanics live here once.
/// </summary>
internal static class InspectionReporter
{
    public static string SafeGameVersion()
    {
        try { return Application.version; }
        catch { return "<unknown>"; }
    }

    public static bool TryGetActiveActor(out Actor actor)
    {
        actor = null;
        try
        {
            var tm = TacticalManager.Get();
            actor = tm != null ? tm.m_ActiveActor : null;
            return actor != null;
        }
        catch
        {
            return false;
        }
    }

    public static InspectionCheck Errored(string name, Exception ex)
        => new() { Name = name, Status = DiagnosticStatus.Error, Detail = $"{ex.GetType().Name}: {ex.Message}" };

    // Append and log immediately, so the result survives a later native crash.
    public static void Emit(InspectionReport report, InspectionCheck check, MelonLogger.Instance log, string label)
    {
        report.Checks.Add(check);
        log.Msg($"[{label}] {check.Name}: {check.Status.ToUpperInvariant()}  {check.Detail}");
    }

    public static void Write(InspectionReport report, string sceneTag, MelonLogger.Instance log, string label)
    {
        var passes = 0;
        var fails = 0;
        foreach (var c in report.Checks)
        {
            if (c.Status == DiagnosticStatus.Pass) passes++;
            else if (c.Status == DiagnosticStatus.Fail || c.Status == DiagnosticStatus.Error) fails++;
        }

        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss");
            var safeTag = InspectionSink.SanitiseForFileName(sceneTag);
            var path = System.IO.Path.Combine(InspectionSink.GetOutputDirectory(), $"{timestamp}-{label}-{safeTag}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, InspectionSink.JsonOptions));
            log.Msg($"[{label}] {passes} passed, {fails} failed/errored, {report.Checks.Count} total. Report: {path}");
        }
        catch (Exception ex)
        {
            log.Error($"[{label}] report write failed: {ex}");
        }
    }
}
