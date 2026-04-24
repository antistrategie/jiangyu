import { describe, expect, it } from "vitest";
import { INITIAL_COMPILE_STATE, type CompileState } from "@lib/compile/compile.ts";
import { computeProgressPct, deriveDisplayStatus, formatDurationLive } from "./statusBarHelpers.ts";

function stateWith(overrides: Partial<CompileState>): CompileState {
  return { ...INITIAL_COMPILE_STATE, ...overrides };
}

describe("computeProgressPct", () => {
  it("returns null when idle", () => {
    expect(computeProgressPct(INITIAL_COMPILE_STATE)).toBeNull();
  });

  it("returns null when running but no progress reported", () => {
    expect(computeProgressPct(stateWith({ status: "running" }))).toBeNull();
  });

  it("returns null when progress total is zero", () => {
    const state = stateWith({ status: "running", progress: { current: 0, total: 0 } });
    expect(computeProgressPct(state)).toBeNull();
  });

  it("returns 0 at the start of progress", () => {
    const state = stateWith({ status: "running", progress: { current: 0, total: 10 } });
    expect(computeProgressPct(state)).toBe(0);
  });

  it("returns 100 when complete", () => {
    const state = stateWith({ status: "running", progress: { current: 10, total: 10 } });
    expect(computeProgressPct(state)).toBe(100);
  });

  it("rounds to nearest integer", () => {
    const state = stateWith({ status: "running", progress: { current: 1, total: 3 } });
    expect(computeProgressPct(state)).toBe(33);
  });

  it("returns null when status is success even with leftover progress", () => {
    const state = stateWith({ status: "success", progress: { current: 5, total: 10 } });
    expect(computeProgressPct(state)).toBeNull();
  });

  it("returns null when status is failed", () => {
    const state = stateWith({ status: "failed", progress: { current: 3, total: 10 } });
    expect(computeProgressPct(state)).toBeNull();
  });
});

describe("deriveDisplayStatus", () => {
  it("returns the compile status when no override is active", () => {
    expect(deriveDisplayStatus("idle", null)).toBe("idle");
    expect(deriveDisplayStatus("running", null)).toBe("running");
    expect(deriveDisplayStatus("success", null)).toBe("success");
    expect(deriveDisplayStatus("failed", null)).toBe("failed");
  });

  it("returns the override when present", () => {
    expect(deriveDisplayStatus("success", "idle")).toBe("idle");
    expect(deriveDisplayStatus("failed", "idle")).toBe("idle");
  });
});

describe("formatDurationLive", () => {
  it("returns 0s when startedAt is null", () => {
    expect(formatDurationLive(INITIAL_COMPILE_STATE)).toBe("0s");
  });

  it("uses finishedAt when available", () => {
    const state = stateWith({ startedAt: 1000, finishedAt: 6000 });
    expect(formatDurationLive(state)).toBe("5s");
  });

  it("formats minutes correctly", () => {
    const state = stateWith({ startedAt: 0, finishedAt: 125_000 });
    expect(formatDurationLive(state)).toBe("2m05s");
  });
});
