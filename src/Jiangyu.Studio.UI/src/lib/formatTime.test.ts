import { describe, expect, it } from "vitest";
import { formatTime } from "./formatTime.ts";

describe("formatTime", () => {
  it("formats zero as 0:00", () => {
    expect(formatTime(0)).toBe("0:00");
  });

  it("formats whole seconds under a minute", () => {
    expect(formatTime(5)).toBe("0:05");
    expect(formatTime(59)).toBe("0:59");
  });

  it("formats exactly one minute", () => {
    expect(formatTime(60)).toBe("1:00");
  });

  it("formats minutes and seconds with zero-padded seconds", () => {
    expect(formatTime(65)).toBe("1:05");
    expect(formatTime(130)).toBe("2:10");
  });

  it("handles durations over an hour", () => {
    expect(formatTime(3661)).toBe("61:01");
  });

  it("truncates fractional seconds (floors, does not round)", () => {
    expect(formatTime(65.9)).toBe("1:05");
    expect(formatTime(0.999)).toBe("0:00");
  });

  it("returns 0:00 for Infinity", () => {
    expect(formatTime(Infinity)).toBe("0:00");
  });

  it("returns 0:00 for negative Infinity", () => {
    expect(formatTime(-Infinity)).toBe("0:00");
  });

  it("returns 0:00 for NaN", () => {
    expect(formatTime(NaN)).toBe("0:00");
  });

  it("returns 0:00 for negative values", () => {
    expect(formatTime(-5)).toBe("0:00");
  });
});
