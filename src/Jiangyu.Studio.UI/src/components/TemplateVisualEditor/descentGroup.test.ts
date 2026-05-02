import { describe, it, expect } from "vitest";
import { groupDirectives, parseDescentPath } from "./helpers";

describe("parseDescentPath", () => {
  it("parses a simple descent path", () => {
    expect(parseDescentPath("EventHandlers[0].ShowHUDText")).toEqual({
      field: "EventHandlers",
      index: 0,
      suffix: "ShowHUDText",
    });
  });

  it("preserves a deeper inner suffix", () => {
    expect(parseDescentPath("EventHandlers[2].Properties[0].Amount")).toEqual({
      field: "EventHandlers",
      index: 2,
      suffix: "Properties[0].Amount",
    });
  });

  it("returns null for a non-descent fieldPath", () => {
    expect(parseDescentPath("EventHandlers")).toBeNull();
    expect(parseDescentPath("EventHandlers[0]")).toBeNull();
    expect(parseDescentPath("Cooldown")).toBeNull();
    expect(parseDescentPath("a.b")).toBeNull();
  });

  it("returns null for malformed bracket expressions", () => {
    expect(parseDescentPath("Field[abc].x")).toBeNull();
    expect(parseDescentPath("Field[].x")).toBeNull();
    expect(parseDescentPath("[0].x")).toBeNull();
  });
});

describe("groupDirectives", () => {
  interface D {
    op: string;
    fieldPath: string;
    subtypeHints?: Record<number, string> | null;
  }

  it("groups consecutive descent directives sharing field+index+subtype", () => {
    const directives: D[] = [
      {
        op: "Set",
        fieldPath: "EventHandlers[0].ShowHUDText",
        subtypeHints: { 0: "AddSkill" },
      },
      {
        op: "Set",
        fieldPath: "EventHandlers[0].OnlyApplyOnHit",
        subtypeHints: { 0: "AddSkill" },
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
        fieldPath: "EventHandlers[0].A",
        subtypeHints: { 0: "AddSkill" },
      },
      {
        op: "Set",
        fieldPath: "EventHandlers[0].B",
        subtypeHints: { 0: "ChangePropertyConditional" },
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
        fieldPath: "EventHandlers[0].ShowHUDText",
        subtypeHints: { 0: "AddSkill" },
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
        fieldPath: "EventHandlers[0].A",
        subtypeHints: { 0: "X" },
      },
      { op: "Set", fieldPath: "Cooldown" },
      {
        op: "Set",
        fieldPath: "EventHandlers[0].B",
        subtypeHints: { 0: "X" },
      },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(3);
    expect(groups[0]?.kind).toBe("group");
    expect(groups[2]?.kind).toBe("group");
  });

  it("treats missing subtype hint as null and groups by null subtype", () => {
    const directives: D[] = [
      { op: "Set", fieldPath: "Items[0].Name" },
      { op: "Set", fieldPath: "Items[0].Cost" },
    ];
    const groups = groupDirectives(directives);
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({ kind: "group", subtype: null });
  });
});
