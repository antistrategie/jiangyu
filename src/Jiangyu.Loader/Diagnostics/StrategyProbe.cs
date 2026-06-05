using System;
using Il2CppMenace.Items;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using MelonLoader;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// On-demand characterisation of the strategy (campaign) layer for the
/// <c>strategy.run</c> bridge request. Confirms the <see cref="StrategyState"/>
/// singleton, reads the campaign resources, round-trips a net-zero
/// <c>ChangeVar</c> (+1 then -1, silent) to validate the resource-mutation path
/// without leaving a lasting change, and reads roster counts. Validates the
/// <c>Jiangyu.Game.Strategy</c> verbs against the live campaign. Must run on the
/// Unity main thread; returns an error object when no campaign is loaded.
/// </summary>
internal static class StrategyProbe
{
    internal static object Capture(MelonLogger.Instance log)
    {
        var state = StrategyState.Get();
        if (state == null)
            return new { error = "not in the strategy layer (StrategyState.Get() is null)" };

        var report = new StrategyReport
        {
            Timestamp = DateTimeOffset.UtcNow,
            SdkLoaderVersion = BuildInfo.Version,
        };

        try
        {
            report.OciComponents = state.GetVar(StrategyVars.OciComponents);
            report.PromotionPoints = state.GetVar(StrategyVars.PromotionPoints);
            report.Intelligence = state.GetVar(StrategyVars.Intelligence);
            report.Authority = state.GetVar(StrategyVars.Authority);
        }
        catch (Exception ex) { report.ResourceError = $"{ex.GetType().Name}: {ex.Message}"; }

        // Net-zero round-trip (silent: no notification, event, or total counter) to confirm
        // ChangeVar mutates and reads back without leaving a lasting change.
        try
        {
            var before = state.GetVar(StrategyVars.OciComponents);
            state.ChangeVar(StrategyVars.OciComponents, 1, false, false, false);
            var mid = state.GetVar(StrategyVars.OciComponents);
            state.ChangeVar(StrategyVars.OciComponents, -1, false, false, false);
            var after = state.GetVar(StrategyVars.OciComponents);
            report.ChangeVarMutates = mid == before + 1;
            report.ChangeVarRoundTrips = after == before;
        }
        catch (Exception ex) { report.ChangeVarError = $"{ex.GetType().Name}: {ex.Message}"; }

        try
        {
            var roster = state.Roster;
            if (roster == null)
            {
                report.RosterError = "StrategyState.Roster is null";
            }
            else
            {
                report.AvailableUnits = roster.GetAvailableUnits();
                report.HasAliveAvailableLeader = roster.HasAliveAvailableLeader();
            }
        }
        catch (Exception ex) { report.RosterError = $"{ex.GetType().Name}: {ex.Message}"; }

        // Destructive: hire then dismiss a leader to characterise the roster-mutation verbs.
        // Opt-in behind `strategy-mutate`, because it alters the persistent roster (the hired
        // leader ends in the dismissed pool, not back in the hirable list).
        if (!DevFlags.IsEnabled("strategy-mutate"))
        {
            report.HireDismiss = "skipped (set the strategy-mutate toggle to characterise hire/dismiss)";
            report.BlackMarket = "skipped (set the strategy-mutate toggle to characterise the black-market refresh)";
        }
        else
        {
            try
            {
                var roster = state.Roster;
                if (roster == null)
                {
                    report.HireDismiss = "roster is null";
                }
                else
                {
                    var hired = roster.GetHiredLeaders()?.TryCast<Il2CppSystem.Collections.Generic.List<BaseUnitLeader>>();
                    if (hired == null || hired.Count == 0)
                    {
                        report.HireDismiss = hired == null ? "GetHiredLeaders cast failed" : "no hired leader to test with";
                    }
                    else
                    {
                        // Dismiss a hired leader, then hire it back from its template, to
                        // exercise both verbs and restore the roster to where it started.
                        var leader = hired[0];
                        var template = leader.LeaderTemplate;
                        var availStart = roster.GetAvailableUnits();
                        var dismissed = roster.TryDismissLeader(leader);
                        var availAfterDismiss = roster.GetAvailableUnits();
                        var rehired = template != null ? roster.HireLeader(template) : null;
                        var availAfterRehire = roster.GetAvailableUnits();
                        report.HireDismiss = $"dismiss->hire round-trip: dismissed={dismissed}, hadTemplate={template != null}, rehired={rehired != null}, availableUnits {availStart} -> {availAfterDismiss} -> {availAfterRehire}";
                    }
                }
            }
            catch (Exception ex) { report.HireDismiss = $"{ex.GetType().Name}: {ex.Message}"; }

            try
            {
                var market = state.BlackMarket;
                if (market == null)
                {
                    report.BlackMarket = "BlackMarket is null";
                }
                else
                {
                    var before = BlackMarketStock(market);
                    market.OnOperationFinished(null);
                    var after = BlackMarketStock(market);
                    report.BlackMarket = $"refresh: regular-stock {before} -> {after}";
                }
            }
            catch (Exception ex) { report.BlackMarket = $"{ex.GetType().Name}: {ex.Message}"; }
        }

        return report;
    }

    private static int BlackMarketStock(BlackMarket market)
    {
        var items = new Il2CppSystem.Collections.Generic.List<BaseItem>();
        market.GetInstances(items, false);
        return items.Count;
    }

    private sealed class StrategyReport
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SdkLoaderVersion { get; set; }
        public int OciComponents { get; set; }
        public int PromotionPoints { get; set; }
        public int Intelligence { get; set; }
        public int Authority { get; set; }
        public string ResourceError { get; set; }
        public bool ChangeVarMutates { get; set; }
        public bool ChangeVarRoundTrips { get; set; }
        public string ChangeVarError { get; set; }
        public int AvailableUnits { get; set; }
        public bool HasAliveAvailableLeader { get; set; }
        public string RosterError { get; set; }
        public string HireDismiss { get; set; }
        public string BlackMarket { get; set; }
    }
}
