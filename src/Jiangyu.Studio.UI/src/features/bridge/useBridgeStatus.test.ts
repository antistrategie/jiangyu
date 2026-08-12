// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The module keeps its poll loop in module scope, so each test needs a fresh copy.
const bridgeStatus = vi.fn();
vi.mock("@features/bridge/bridge", () => ({
  bridgeStatus: () => bridgeStatus() as Promise<unknown>,
}));

async function loadModule() {
  vi.resetModules();
  return import("./useBridgeStatus");
}

/** A promise plus the handle to settle it, so a test can hold a poll in flight. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

const connected = { enabled: true, connected: true };

describe("useBridgeStatus poll loop", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    bridgeStatus.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // The bug this guards: every RPC serialises under one lock on the host, so a
  // slow reply used to let fixed-interval ticks queue behind it without bound.
  // The backlog is what made the app crawl while Settings sat open.
  it("does not start a second request while one is in flight", async () => {
    const first = deferred<typeof connected>();
    bridgeStatus.mockReturnValueOnce(first.promise).mockResolvedValue(connected);
    const { subscribeForTest } = await loadModule();

    subscribeForTest(() => {});
    expect(bridgeStatus).toHaveBeenCalledTimes(1);

    // Well past several poll gaps, with the host still not having replied.
    await vi.advanceTimersByTimeAsync(20_000);
    expect(bridgeStatus).toHaveBeenCalledTimes(1);

    first.resolve(connected);
    await vi.advanceTimersByTimeAsync(0);
    // Only now does the gap to the next request begin.
    expect(bridgeStatus).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(2000);
    expect(bridgeStatus).toHaveBeenCalledTimes(2);
  });

  it("keeps polling on a cadence once replies are prompt", async () => {
    bridgeStatus.mockResolvedValue(connected);
    const { subscribeForTest } = await loadModule();

    subscribeForTest(() => {});
    await vi.advanceTimersByTimeAsync(0);
    expect(bridgeStatus).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(2000);
    expect(bridgeStatus).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(2000);
    expect(bridgeStatus).toHaveBeenCalledTimes(3);
  });

  it("stops once the last subscriber leaves", async () => {
    bridgeStatus.mockResolvedValue(connected);
    const { subscribeForTest } = await loadModule();

    const unsubscribe = subscribeForTest(() => {});
    await vi.advanceTimersByTimeAsync(0);
    expect(bridgeStatus).toHaveBeenCalledTimes(1);

    unsubscribe();
    await vi.advanceTimersByTimeAsync(20_000);
    expect(bridgeStatus).toHaveBeenCalledTimes(1);
  });

  // Unsubscribing mid-request orphans that request. Re-subscribing has to start
  // exactly one loop, not leave the orphan running alongside the new one.
  it("runs a single loop when resubscribed while a request is in flight", async () => {
    const held = deferred<typeof connected>();
    bridgeStatus.mockReturnValueOnce(held.promise).mockResolvedValue(connected);
    const { subscribeForTest } = await loadModule();

    const unsubscribe = subscribeForTest(() => {});
    expect(bridgeStatus).toHaveBeenCalledTimes(1);

    unsubscribe();
    subscribeForTest(() => {});
    expect(bridgeStatus).toHaveBeenCalledTimes(2);

    // The orphaned first request settling must not schedule a tick of its own.
    held.resolve(connected);
    await vi.advanceTimersByTimeAsync(2000);
    expect(bridgeStatus).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(2000);
    expect(bridgeStatus).toHaveBeenCalledTimes(4);
  });

  // A failing bridge must not become a hot loop: the gap applies to a rejection
  // exactly as it does to a reply.
  it("waits the same gap after a failed request", async () => {
    bridgeStatus.mockRejectedValue(new Error("not connected"));
    const { subscribeForTest } = await loadModule();

    subscribeForTest(() => {});
    await vi.advanceTimersByTimeAsync(0);
    expect(bridgeStatus).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(1999);
    expect(bridgeStatus).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(bridgeStatus).toHaveBeenCalledTimes(2);
  });
});
