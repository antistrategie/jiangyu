using System.Globalization;
using System.Text;
using Il2CppMenace.Tactical;

namespace Jiangyu.Loader.Net;

// The canonical per-actor state line, shared by the determinism snapshot (record/replay
// hashing) and the net mission checksum (cross-peer hashing) so the two render an actor
// identically and never drift. One actor becomes one ordinal-sortable line: identity
// (faction, template, id, tile) then vitals. Mission-level fields and RNG state are the
// caller's concern, not this line. Reads defensively so one throwing getter marks its
// field rather than losing the whole line.
internal static class ActorProjection
{
    public static string Line(Actor actor)
    {
        var sb = new StringBuilder(160);
        sb.Append("actor|").Append(Safe(() => actor.GetFaction().ToString()));
        sb.Append('|').Append(Safe(() => actor.GetTemplate()?.name ?? "<null>"));
        sb.Append("|id=").Append(Safe(() => actor.ID.ToString(CultureInfo.InvariantCulture)));
        sb.Append('|').Append(SafeTile(actor));
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

    public static string SafeTile(Actor actor)
        => Safe(() =>
        {
            var tile = actor.GetTile();
            return tile == null ? "<null>" : $"{tile.GetX()},{tile.GetZ()}";
        });

    public static string Safe(Func<string> read)
    {
        try { return read() ?? "<null>"; }
        catch (Exception ex) { return $"<threw:{ex.GetType().Name}>"; }
    }
}
