import { describe, expect, it } from "vitest";
import { computeProgressPct, deriveDisplayStatus, formatDurationLive } from "./statusBarHelpers";

describe("computeProgressPct", () => {
  it("returns null when idle", () => {
    expect(computeProgressPct("idle", null)).toBeNull();
  });

  it("returns null when running but no progress reported", () => {
    expect(computeProgressPct("running", null)).toBeNull();
  });

  it("returns null when progress total is zero", () => {
    expect(computeProgressPct("running", { current: 0, total: 0 })).toBeNull();
  });

  it("returns 0 at the start of progress", () => {
    expect(computeProgressPct("running", { current: 0, total: 10 })).toBe(0);
  });

  it("returns 100 when complete", () => {
    expect(computeProgressPct("running", { current: 10, total: 10 })).toBe(100);
  });

  it("rounds to nearest integer", () => {
    expect(computeProgressPct("running", { current: 1, total: 3 })).toBe(33);
  });

  it("returns null when status is success even with leftover progress", () => {
    expect(computeProgressPct("success", { current: 5, total: 10 })).toBeNull();
  });

  it("returns null when status is failed", () => {
    expect(computeProgressPct("failed", { current: 3, total: 10 })).toBeNull();
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
    expect(formatDurationLive(null, null)).toBe("0s");
  });

  it("uses finishedAt when available", () => {
    expect(formatDurationLive(1000, 6000)).toBe("5s");
  });

  it("formats minutes correctly", () => {
    expect(formatDurationLive(0, 125_000)).toBe("2m05s");
  });
});
