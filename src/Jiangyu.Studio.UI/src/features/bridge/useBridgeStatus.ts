import { useSyncExternalStore, type Dispatch, type SetStateAction } from "react";
import { bridgeStatus, type BridgeStatusResult } from "@features/bridge/bridge";

// A single shared poll of the bridge: every consumer reads the same status and only one
// poll loop runs no matter how many components subscribe.
//
// The gap is measured from the end of one request to the start of the next rather
// than on a fixed interval. Every RPC serialises under one lock on the host, so a
// slow one (an indexing pass, a stalled bridge connect) delays the reply. On a fixed
// interval the requests would keep being posted regardless and queue up behind it,
// growing a backlog that never drains while the poll is subscribed. That backlog is
// what makes the app crawl and take seconds to respond to anything else.
const POLL_GAP_MS = 2000;

let current: BridgeStatusResult | null = null;
let epoch = 0;
let timer: number | null = null;
// Bumped whenever the loop stops, so a request still in flight at that moment
// cannot schedule the next tick and leave a second loop running.
let generation = 0;
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

function poll(forGeneration: number): void {
  timer = null;
  // Ignore a result that an explicit setStatus superseded while it was in flight.
  const dispatched = epoch;
  void bridgeStatus()
    .then((s) => {
      if (dispatched === epoch) apply(s);
    })
    .catch(() => {
      if (dispatched === epoch) apply(null);
    })
    .finally(() => {
      if (forGeneration !== generation) return;
      timer = window.setTimeout(() => poll(forGeneration), POLL_GAP_MS);
    });
}

function stop(): void {
  generation += 1;
  if (timer !== null) {
    window.clearTimeout(timer);
    timer = null;
  }
}

function subscribe(listener: () => void): () => void {
  const first = listeners.size === 0;
  listeners.add(listener);
  if (first) {
    generation += 1;
    poll(generation);
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0) stop();
  };
}

/** The raw subscription, exposed so tests can drive the loop without mounting a component. */
export const subscribeForTest = subscribe;

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
