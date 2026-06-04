import { useSyncExternalStore, type Dispatch, type SetStateAction } from "react";
import { bridgeStatus, type BridgeStatusResult } from "@features/bridge/bridge";

// A single shared poll of the bridge: every consumer reads the same status and only one
// 2s interval runs no matter how many components subscribe.
let current: BridgeStatusResult | null = null;
let epoch = 0;
let interval: number | null = null;
const listeners = new Set<() => void>();

function sameStatus(a: BridgeStatusResult | null, b: BridgeStatusResult | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.enabled === b.enabled && a.connected === b.connected;
}

function apply(next: BridgeStatusResult | null): void {
  if (sameStatus(current, next)) return;
  current = next;
  for (const listener of listeners) listener();
}

function poll(): void {
  // Ignore a result that an explicit setStatus superseded while it was in flight.
  const dispatched = epoch;
  void bridgeStatus()
    .then((s) => {
      if (dispatched === epoch) apply(s);
    })
    .catch(() => {
      if (dispatched === epoch) apply(null);
    });
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (interval === null) {
    poll();
    interval = window.setInterval(poll, 2000);
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && interval !== null) {
      window.clearInterval(interval);
      interval = null;
    }
  };
}

/** Push an authoritative status (e.g. after toggling the bridge), superseding any in-flight poll. */
export const setBridgeStatus: Dispatch<SetStateAction<BridgeStatusResult | null>> = (action) => {
  epoch += 1;
  apply(typeof action === "function" ? action(current) : action);
};

/**
 * Subscribes to the shared bridge poll. Returns the latest status (null before the first poll,
 * or after an error) and a setter so callers that mutate the bridge can push the result.
 */
export function useBridgeStatus(): {
  status: BridgeStatusResult | null;
  setStatus: Dispatch<SetStateAction<BridgeStatusResult | null>>;
} {
  const status = useSyncExternalStore(subscribe, () => current);
  return { status, setStatus: setBridgeStatus };
}
