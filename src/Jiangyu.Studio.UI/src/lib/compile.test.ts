import { describe, expect, it } from "vitest";
import { formatDurationShort, buildSuccessDetail, INITIAL_COMPILE_STATE } from "./compile.ts";

describe("formatDurationShort", () => {
  it("formats zero as 0s", () => {
    expect(formatDurationShort(0)).toBe("0s");
  });

  it("formats seconds under a minute", () => {
    expect(formatDurationShort(5_000)).toBe("5s");
    expect(formatDurationShort(59_000)).toBe("59s");
  });

  it("formats exactly one minute", () => {
    expect(formatDurationShort(60_000)).toBe("1m00s");
  });

  it("formats minutes with zero-padded seconds", () => {
    expect(formatDurationShort(65_000)).toBe("1m05s");
    expect(formatDurationShort(135_000)).toBe("2m15s");
  });

  it("handles durations over an hour", () => {
    expect(formatDurationShort(3_661_000)).toBe("61m01s");
  });

  it("truncates sub-second precision", () => {
    expect(formatDurationShort(1_999)).toBe("1s");
    expect(formatDurationShort(500)).toBe("0s");
  });
});

describe("buildSuccessDetail", () => {
  it("returns null when all inputs are empty", () => {
    expect(buildSuccessDetail(null, 0, null)).toBeNull();
  });

  it("returns duration only", () => {
    expect(buildSuccessDetail("12s", 0, null)).toBe("12s");
  });

  it("returns warnings only", () => {
    expect(buildSuccessDetail(null, 3, null)).toBe("3 warnings");
  });

  it("pluralises single warning", () => {
    expect(buildSuccessDetail(null, 1, null)).toBe("1 warning");
  });

  it("returns bundle path only", () => {
    expect(buildSuccessDetail(null, 0, "/out/mod.bundle")).toBe("/out/mod.bundle");
  });

  it("joins all parts with middle dot", () => {
    expect(buildSuccessDetail("5s", 2, "/out/mod.bundle")).toBe(
      "5s · 2 warnings · /out/mod.bundle",
    );
  });

  it("joins duration and warnings without path", () => {
    expect(buildSuccessDetail("1m05s", 1, null)).toBe("1m05s · 1 warning");
  });
});

describe("INITIAL_COMPILE_STATE", () => {
  it("starts idle with empty logs", () => {
    expect(INITIAL_COMPILE_STATE.status).toBe("idle");
    expect(INITIAL_COMPILE_STATE.logs).toEqual([]);
    expect(INITIAL_COMPILE_STATE.phase).toBeNull();
    expect(INITIAL_COMPILE_STATE.startedAt).toBeNull();
    expect(INITIAL_COMPILE_STATE.finishedAt).toBeNull();
    expect(INITIAL_COMPILE_STATE.bundlePath).toBeNull();
    expect(INITIAL_COMPILE_STATE.errorMessage).toBeNull();
  });
});
