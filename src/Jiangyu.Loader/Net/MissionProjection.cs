using Il2CppMenace.Tactical;
using Jiangyu.Shared.Net.Sync;

namespace Jiangyu.Loader.Net;

// The sim-state projection the barrier checksum hashes, partitioned by faction so a
// desync report names the diverging faction. Per-actor lines come from the shared
// ActorProjection (identical to the determinism snapshot's), and the RNG fragment is
// deliberately omitted: it never matches across processes and is not sim state. Reads
// defensively so the projection is always total and comparable.
internal static class MissionProjection
{
    public static List<ChecksumPartition> Build(TacticalManager manager)
    {
        var partitions = new List<ChecksumPartition>();

        // The whole faction walk is guarded: once a mission ends the manager's native side
        // is gone and GetFactions throws, and the checksum loop must degrade to an empty
        // (comparable) projection rather than throw out of the session coroutine.
        try
        {
            var factions = manager.GetFactions();
            for (var i = 0; factions != null && i < factions.Length; i++)
            {
                var faction = factions[i];
                if (faction == null)
                    continue;

                var name = ActorProjection.Safe(() => "faction:" + faction.GetFactionType());
                var lines = new List<string>();
                try
                {
                    var actors = faction.GetActors();
                    for (var j = 0; actors != null && j < actors.Count; j++)
                    {
                        var actor = actors[j];
                        if (actor == null)
                            continue;
                        try { lines.Add(ActorProjection.Line(actor)); }
                        catch (Exception ex) { lines.Add($"actor|<threw:{ex.GetType().Name}>"); }
                    }
                }
                catch (Exception ex)
                {
                    lines.Add($"actors|<threw:{ex.GetType().Name}>");
                }

                lines.Sort(StringComparer.Ordinal);
                partitions.Add(new ChecksumPartition(name, lines));
            }
        }
        catch (Exception ex)
        {
            partitions.Add(new ChecksumPartition("factions", new[] { $"<threw:{ex.GetType().Name}>" }));
        }

        return partitions;
    }
}
