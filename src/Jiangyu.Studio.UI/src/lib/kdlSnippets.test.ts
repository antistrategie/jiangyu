import { describe, expect, it } from "vitest";
import { generatePatchKdl, generateCloneKdl } from "./kdlSnippets.ts";

describe("generatePatchKdl", () => {
  it("generates a patch block with type and id", () => {
    const result = generatePatchKdl("EntityTemplate", "player_squad.darby");
    expect(result).toContain('patch "EntityTemplate" "player_squad.darby"');
    expect(result).toContain("{");
    expect(result).toContain("}");
    expect(result).toMatch(/\n$/);
  });

  it("escapes nothing — passes through raw strings", () => {
    const result = generatePatchKdl("WeaponTemplate", "weapon.carbine_tier1");
    expect(result).toContain('"WeaponTemplate"');
    expect(result).toContain('"weapon.carbine_tier1"');
  });
});

describe("generateCloneKdl", () => {
  it("generates a clone block with from and id properties", () => {
    const result = generateCloneKdl(
      "UnitLeaderTemplate",
      "squad_leader.darby",
      "squad_leader.darby_clone",
    );
    expect(result).toContain('clone "UnitLeaderTemplate"');
    expect(result).toContain('from="squad_leader.darby"');
    expect(result).toContain('id="squad_leader.darby_clone"');
    expect(result).toContain("{");
    expect(result).toContain("}");
    expect(result).toMatch(/\n$/);
  });
});
