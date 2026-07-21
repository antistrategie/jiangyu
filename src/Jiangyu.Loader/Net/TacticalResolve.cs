using Il2CppMenace.Tactical;
using Il2CppMenace.Tactical.Skills;

namespace Jiangyu.Loader.Net;

// Shared tactical lookups used by both command drivers: the net command applier and the
// determinism harness. Actor resolution is not here because it differs by driver (by
// Entity.ID for replication, by faction/template for scripts); tile and skill resolution
// are identical, so they live once.
internal static class TacticalResolve
{
    public static Tile Tile(TacticalManager manager, int x, int z) => manager.GetMap()?.GetTile(x, z);

    public static Skill Skill(Actor actor, string skillId)
    {
        var all = actor.GetSkills()?.GetAllSkills();
        for (var i = 0; all != null && i < all.Count; i++)
        {
            var skill = all[i]?.TryCast<Skill>();
            if (skill != null && string.Equals(skill.GetID(), skillId, StringComparison.Ordinal))
                return skill;
        }

        return null;
    }
}
