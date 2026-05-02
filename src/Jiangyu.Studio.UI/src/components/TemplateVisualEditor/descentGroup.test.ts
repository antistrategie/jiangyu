import { describe, it, expect } from "vitest";
import { groupDirectives } from "./helpers";
import type { DescentStep } from "./types";

describe("groupDirectives", () => {
  interface D {
    op: string;
    fieldPath: string;
    descent?: DescentStep[];
  }

  it("groups consecutive descent directives sharing field+index+subtype", () => {
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "ShowHUDText",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
      {
        op: "Set",
        fieldPath: "OnlyApplyOnHit",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({
      kind: "group",
      field: "EventHandlers",
      index: 0,
      subtype: "AddSkill",
      members: [{ suffix: "ShowHUDText" }, { suffix: "OnlyApplyOnHit" }],
    });
  });

  it("splits when subtype hints differ at segment 0", () => {
    // Same (field, index) but different subtype — must stay separate so
    // the serialiser emits two outer descent blocks (the validated subtype
    // changes mid-stream and folding them would silently relabel ops).
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "A",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
      {
        op: "Set",
        fieldPath: "B",
        descent: [{ field: "EventHandlers", index: 0, subtype: "ChangePropertyConditional" }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(2);
  });

  it("interleaves loose directives between groups", () => {
    const directives: D[] = [
      { op: "Set", fieldPath: "Cooldown" },
      {
        op: "Set",
        fieldPath: "ShowHUDText",
        descent: [{ field: "EventHandlers", index: 0, subtype: "AddSkill" }],
      },
      { op: "Append", fieldPath: "Tags" },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(3);
    expect(groups[0]?.kind).toBe("loose");
    expect(groups[1]?.kind).toBe("group");
    expect(groups[2]?.kind).toBe("loose");
  });

  it("does not merge non-consecutive descent directives", () => {
    // Modder split them with a loose op in the middle; respect their
    // ordering — the serialiser emits them as separate outer blocks.
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "A",
        descent: [{ field: "EventHandlers", index: 0, subtype: "X" }],
      },
      { op: "Set", fieldPath: "Cooldown" },
      {
        op: "Set",
        fieldPath: "B",
        descent: [{ field: "EventHandlers", index: 0, subtype: "X" }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(3);
    expect(groups[0]?.kind).toBe("group");
    expect(groups[2]?.kind).toBe("group");
  });

  it("treats missing subtype hint as null and groups by null subtype", () => {
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "Name",
        descent: [{ field: "Items", index: 0 }],
      },
      {
        op: "Set",
        fieldPath: "Cost",
        descent: [{ field: "Items", index: 0 }],
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({ kind: "group", subtype: null });
  });
});
