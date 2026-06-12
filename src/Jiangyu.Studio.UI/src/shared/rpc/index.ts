/**
 * JSON-RPC client for communicating with the C# host via InfiniFrame messaging.
 *
 * Messages from the host are either:
 *   - responses: `{id, result?, error?}` — match a pending request by id
 *   - notifications: `{method, params?}` — no id; routed to subscribers
 */

import type { HostNotificationMap } from "./notifications";

interface RpcResponse {
  readonly id: number;
  readonly result?: unknown;
  // Host serialises absent errors as `null`, not as a missing field, so the
  // type has to allow null too — otherwise runtime `{error: null}` payloads
  // get misclassified as failures (see the explicit null check below).
  readonly error?: string | null;
}

interface RpcNotification {
  readonly method: string;
  readonly params?: unknown;
}

type RpcMessage = RpcResponse | RpcNotification;

function isRpcResponse(msg: RpcMessage): msg is RpcResponse {
  return typeof (msg as RpcResponse).id === "number";
}

function isRpcNotification(msg: RpcMessage): msg is RpcNotification {
  return typeof (msg as RpcNotification).method === "string";
}

interface PendingRequest {
  readonly resolve: (value: unknown) => void;
  readonly reject: (reason: Error) => void;
}

type NotificationCallback = (params: unknown) => void;

interface InfiniFrameHost {
  postMessage: (msg: string) => void;
  receiveMessage: (callback: (msg: string) => void) => void;
}

let nextId = 1;
const pending = new Map<number, PendingRequest>();
const subscribers = new Map<string, Set<NotificationCallback>>();

// The native layer injects `window.infiniframe` with a host bridge exposing
// postData / receiveCallback. Every JS→host post is wrapped in an
// {id, command, data, version: 2} envelope which the host's keyed-message
// dispatcher routes by `id`.
interface InfiniFrameBridge {
  postData: (envelope: unknown) => void;
  receiveCallback: (cb: (raw: string) => void) => void;
}

interface BridgedWindow {
  infiniframe?: { host?: InfiniFrameBridge };
}

function getHost(): InfiniFrameHost | undefined {
  const win = window as Window & BridgedWindow;
  const bridge = win.infiniframe?.host;
  if (!bridge) return undefined;

  return {
    postMessage: (msg: string) => {
      bridge.postData({
        id: "rpc",
        command: "Post",
        data: msg,
        version: 2,
      });
    },
    receiveMessage: (cb: (msg: string) => void) => {
      bridge.receiveCallback(cb);
    },
  };
}

/**
 * Initialise the RPC listener. Call once at app startup.
 */
export function initRpc(): void {
  const host = getHost();
  if (!host) {
    console.warn("[RPC] InfiniFrame host not available");
    return;
  }

  host.receiveMessage((data: string) => {
    let msg: RpcMessage;
    try {
      msg = JSON.parse(data) as RpcMessage;
    } catch {
      return;
    }

    if (isRpcResponse(msg)) {
      const req = pending.get(msg.id);
      if (!req) return;
      pending.delete(msg.id);
      if (msg.error != null) {
        req.reject(new Error(msg.error));
      } else {
        req.resolve(msg.result);
      }
      return;
    }

    if (isRpcNotification(msg)) {
      const subs = subscribers.get(msg.method);
      if (subs) for (const cb of subs) cb(msg.params);
    }
  });
}

export interface RpcCallOptions {
  /**
   * Milliseconds before the call rejects and its pending entry is dropped,
   * so a lost host response surfaces as an error instead of a promise that
   * hangs forever (wedging any UI flag cleared in its `finally`). Pass 0 for
   * calls that are legitimately unbounded: interactive dialogs, indexing,
   * Unity batchmode, build invocations.
   */
  readonly timeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 120_000;

/**
 * Call a method on the C# host and await the response.
 */
export function rpcCall<T = unknown>(
  method: string,
  params?: unknown,
  options?: RpcCallOptions,
): Promise<T> {
  const host = getHost();
  if (!host) {
    return Promise.reject(new Error("InfiniFrame host not available (running in browser?)"));
  }

  const id = nextId++;
  const message = JSON.stringify({ method, params, id });
  const timeoutMs = options?.timeoutMs ?? DEFAULT_TIMEOUT_MS;

  return new Promise<T>((resolve, reject) => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    if (timeoutMs > 0) {
      timer = setTimeout(() => {
        pending.delete(id);
        reject(new Error(`RPC ${method} timed out after ${(timeoutMs / 1000).toString()}s`));
      }, timeoutMs);
    }
    pending.set(id, {
      resolve: (value) => {
        if (timer !== undefined) clearTimeout(timer);
        resolve(value as T);
      },
      reject: (reason) => {
        if (timer !== undefined) clearTimeout(timer);
        reject(reason);
      },
    });
    host.postMessage(message);
  });
}

export type * from "./types";
export type * from "./notifications";

/**
 * Subscribe to a notification method pushed from the host. Methods and
 * payload shapes come from `HostNotificationMap` (see ./notifications);
 * features that own a payload shape register it there via declaration
 * merging. Returns an unsubscribe function.
 */
export function subscribe<K extends keyof HostNotificationMap>(
  method: K,
  callback: (params: HostNotificationMap[K]) => void,
): () => void {
  let set = subscribers.get(method);
  if (!set) {
    set = new Set();
    subscribers.set(method, set);
  }
  const cb = callback as NotificationCallback;
  set.add(cb);
  return () => {
    const s = subscribers.get(method);
    if (!s) return;
    s.delete(cb);
    if (s.size === 0) subscribers.delete(method);
  };
}
