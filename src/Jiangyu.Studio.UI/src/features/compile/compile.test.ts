import { beforeEach, describe, expect, it } from "vitest";
import {
  formatDurationShort,
  buildSuccessDetail,
  INITIAL_COMPILE_STATE,
  MAX_RETAINED_LOGS,
  useCompileStore,
} from "./index";

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
    expect(INITIAL_COMPILE_STATE.droppedLogCount).toBe(0);
    expect(INITIAL_COMPILE_STATE.warnCount).toBe(0);
    expect(INITIAL_COMPILE_STATE.errorCount).toBe(0);
  });
});

describe("useCompileStore", () => {
  beforeEach(() => {
    useCompileStore.getState().reset();
  });

  it("handleStarted resets accumulators and flips to running", () => {
    useCompileStore.getState().handleLog("warn", "stale");
    useCompileStore.getState().handleStarted();
    const s = useCompileStore.getState();
    expect(s.status).toBe("running");
    expect(s.logs).toHaveLength(0);
    expect(s.warnCount).toBe(0);
    expect(s.startedAt).not.toBeNull();
    expect(s.finishedAt).toBeNull();
  });

  it("handlePhase records the phase and clears the status line", () => {
    useCompileStore.getState().handleStatusLine("building atlas 3/9");
    useCompileStore.getState().handlePhase("Templates");
    const s = useCompileStore.getState();
    expect(s.phase).toBe("Templates");
    expect(s.statusLine).toBeNull();
  });

  it("handleProgress treats a zero total as no progress", () => {
    useCompileStore.getState().handleProgress(0, 0);
    expect(useCompileStore.getState().progress).toBeNull();
    useCompileStore.getState().handleProgress(2, 10);
    expect(useCompileStore.getState().progress).toEqual({ current: 2, total: 10 });
  });

  it("handleLog appends entries and counts warnings and errors", () => {
    const store = useCompileStore.getState();
    store.handleLog("info", "a");
    store.handleLog("warn", "b");
    store.handleLog("error", "c");
    const s = useCompileStore.getState();
    expect(s.logs.map((l) => l.message)).toEqual(["a", "b", "c"]);
    expect(s.warnCount).toBe(1);
    expect(s.errorCount).toBe(1);
  });

  it("handleLog caps the retained list and counts dropped entries", () => {
    const store = useCompileStore.getState();
    for (let i = 0; i < MAX_RETAINED_LOGS + 5; i++) {
      store.handleLog("info", `line ${i}`);
    }
    const s = useCompileStore.getState();
    expect(s.logs).toHaveLength(MAX_RETAINED_LOGS);
    expect(s.droppedLogCount).toBe(5);
    expect(s.logs[0]?.message).toBe("line 5");
    expect(s.logs[s.logs.length - 1]?.message).toBe(`line ${MAX_RETAINED_LOGS + 4}`);
  });

  it("handleFinished records the outcome and clears progress", () => {
    useCompileStore.getState().handleStarted();
    useCompileStore.getState().handleProgress(5, 10);
    useCompileStore.getState().handleFinished(true, "/out/mod.bundle", null);
    const s = useCompileStore.getState();
    expect(s.status).toBe("success");
    expect(s.bundlePath).toBe("/out/mod.bundle");
    expect(s.progress).toBeNull();
    expect(s.finishedAt).not.toBeNull();
  });

  it("handleFinished failure carries the error message", () => {
    useCompileStore.getState().handleStarted();
    useCompileStore.getState().handleFinished(false, null, "unity exited 1");
    const s = useCompileStore.getState();
    expect(s.status).toBe("failed");
    expect(s.errorMessage).toBe("unity exited 1");
  });
});
