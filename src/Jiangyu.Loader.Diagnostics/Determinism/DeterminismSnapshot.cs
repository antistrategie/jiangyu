using System.Globalization;
using System.Text;
using Il2CppMenace.Tactical;

namespace Jiangyu.Loader.Diagnostics.Determinism;

// The state projection hashed at every action barrier. Kept deliberately small and
// order-independent: mission-level turn position plus one canonical line per actor
// (identity, tile, vitals, turn flags), sorted ordinal. The hash is FNV-1a 64 over the
// joined lines; the full line list is journaled alongside so a replay mismatch can show
// exactly which actor and field diverged, not just that one did.
internal sealed class DeterminismSnapshot
{
    public int Round;
    public string ActiveFaction;
    public string ActiveActor;
    public string RngState;
    public List<string> ActorLines = new();

    public string Hash { get; private set; }

    public static DeterminismSnapshot Capture(TacticalManager manager)
    {
        var snap = new DeterminismSnapshot();
        try { snap.Round = manager.GetRound(); } catch { snap.Round = -1; }
        try { snap.ActiveFaction = manager.GetActiveFactionID().ToString(CultureInfo.InvariantCulture); } catch { snap.ActiveFaction = "<threw>"; }
        try
        {
            var active = manager.m_ActiveActor;
            snap.ActiveActor = active == null ? "<none>" : Describe(active);
        }
        catch { snap.ActiveActor = "<threw>"; }

        // The faction walk itself can throw once the mission has ended and the manager's
        // native side is gone (a scripted step that finishes the mission). Capture must
        // stay total: a dead coroutine loses the journal and leaves the session attached,
        // so a broken walk becomes a marker line, comparable like any other.
        try
        {
            var factions = manager.GetFactions();
            for (var i = 0; factions != null && i < factions.Length; i++)
            {
                var actors = factions[i]?.GetActors();
                for (var j = 0; actors != null && j < actors.Count; j++)
                {
                    var actor = actors[j];
                    if (actor == null)
                        continue;
                    // One throwing actor must not abandon the snapshot: record the throw as
                    // its line so a broken read is itself a comparable value.
                    try { snap.ActorLines.Add(Line(actor)); }
                    catch (Exception ex) { snap.ActorLines.Add($"actor|<threw:{ex.GetType().Name}>"); }
                }
            }
        }
        catch (Exception ex)
        {
            snap.ActorLines.Add($"factions|<threw:{ex.GetType().Name}>");
        }
        snap.ActorLines.Sort(StringComparer.Ordinal);
        // The RNG stream position, readable through the interop wrapper. If every actor
        // line matches but this diverges, the sim consumed randomness differently.
        snap.RngState = ReadUnityRandomState();
        snap.Hash = snap.ComputeHash();
        return snap;
    }

    private static string Line(Actor actor)
    {
        var sb = new StringBuilder(128);
        sb.Append("actor|").Append(Safe(() => actor.GetFaction().ToString()));
        sb.Append('|').Append(Safe(() => actor.GetTemplate()?.name ?? "<null>"));
        var tile = SafeTile(actor);
        sb.Append('|').Append(tile);
        sb.Append("|hp=").Append(Safe(() => actor.GetHitpoints().ToString(CultureInfo.InvariantCulture)));
        sb.Append("|ap=").Append(Safe(() => actor.GetActionPoints().ToString(CultureInfo.InvariantCulture)));
        sb.Append("|morale=").Append(Safe(() => actor.GetMorale().ToString("R", CultureInfo.InvariantCulture)));
        sb.Append("|supp=").Append(Safe(() => actor.GetSuppressionPct().ToString("R", CultureInfo.InvariantCulture)));
        sb.Append("|armour=").Append(Safe(() => actor.GetArmorDurability().ToString(CultureInfo.InvariantCulture)));
        sb.Append("|stance=").Append(Safe(() => actor.GetStance().ToString()));
        sb.Append("|alive=").Append(Safe(() => actor.IsAlive().ToString()));
        sb.Append("|acted=").Append(Safe(() => actor.HasActed().ToString()));
        sb.Append("|turndone=").Append(Safe(() => actor.IsTurnDone().ToString()));
        return sb.ToString();
    }

    // Identity fragment for the active-actor line: template + tile, no vitals.
    private static string Describe(Actor actor)
        => $"{Safe(() => actor.GetTemplate()?.name ?? "<null>")}@{SafeTile(actor)}";

    private static string SafeTile(Actor actor)
        => Safe(() =>
        {
            var tile = actor.GetTile();
            return tile == null ? "<null>" : $"{tile.GetX()},{tile.GetZ()}";
        });

    private static string Safe(Func<string> read)
    {
        try { return read() ?? "<null>"; }
        catch (Exception ex) { return $"<threw:{ex.GetType().Name}>"; }
    }

    // UnityEngine.Random.state is a value-type seed block (s0..s3). Read defensively:
    // a throw here must degrade to a marker, never fail the snapshot.
    private static string ReadUnityRandomState()
    {
        try
        {
            var state = UnityEngine.Random.state;
            return $"{state.s0},{state.s1},{state.s2},{state.s3}";
        }
        catch (Exception ex)
        {
            return $"<threw:{ex.GetType().Name}>";
        }
    }

    private string ComputeHash()
    {
        var sb = new StringBuilder();
        sb.Append("mission|round=").Append(Round)
          .Append("|activeFaction=").Append(ActiveFaction)
          .Append("|activeActor=").Append(ActiveActor)
          .Append("|rng=").Append(RngState).Append('\n');
        foreach (var line in ActorLines)
            sb.Append(line).Append('\n');
        return Fnv1a(sb.ToString()).ToString("x16", CultureInfo.InvariantCulture);
    }

    // FNV-1a 64 over UTF-8. Platform-stable, no crypto needed for a divergence detector.
    private static ulong Fnv1a(string text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    // Field-level diff for the replay report: the actor lines present on only one side.
    public static object Diff(string missionReference, List<string> referenceLines, string missionActual, List<string> actualLines)
    {
        var onlyReference = referenceLines.Except(actualLines, StringComparer.Ordinal).ToList();
        var onlyActual = actualLines.Except(referenceLines, StringComparer.Ordinal).ToList();
        return new
        {
            missionReference,
            missionActual,
            onlyInJournal = onlyReference,
            onlyInReplay = onlyActual,
        };
    }
}
