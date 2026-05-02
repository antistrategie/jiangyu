/**
 * JSON-RPC client for communicating with the C# host via InfiniFrame messaging.
 *
 * Messages from the host are either:
 *   - responses: `{id, result?, error?}` — match a pending request by id
 *   - notifications: `{method, params?}` — no id; routed to subscribers
 */

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

export type FileChangeKind = "changed" | "deleted";

export interface FileChangedEvent {
  readonly path: string;
  readonly kind: FileChangeKind;
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

// InfiniFrame 0.11.0 envelope bridge. The native layer injects
// window.__infiniframe with postData/receiveCallback; messages are
// wrapped in {id, command, data, version: 2} envelopes.
interface InfiniFrameBridge {
  postData: (envelope: unknown) => void;
  receiveCallback: (cb: (raw: string) => void) => void;
}

interface BridgedWindow {
  __infiniframe?: { host?: InfiniFrameBridge };
}

function getHost(): InfiniFrameHost | undefined {
  const win = window as Window & BridgedWindow;
  const bridge = win.__infiniframe?.host;
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

/**
 * Call a method on the C# host and await the response.
 */
export function rpcCall<T = unknown>(method: string, params?: unknown): Promise<T> {
  const host = getHost();
  if (!host) {
    return Promise.reject(new Error("InfiniFrame host not available (running in browser?)"));
  }

  const id = nextId++;
  const message = JSON.stringify({ method, params, id });

  return new Promise<T>((resolve, reject) => {
    pending.set(id, {
      resolve: resolve as (value: unknown) => void,
      reject,
    });
    host.postMessage(message);
  });
}

export type * from "./types";

/**
 * Subscribe to a notification method pushed from the host. The payload is
 * `unknown`; callers that know the shape should narrow with a type assertion
 * at the boundary (the host is the source of truth for the wire schema).
 * Returns an unsubscribe function.
 */
export function subscribe(method: string, callback: (params: unknown) => void): () => void {
  let set = subscribers.get(method);
  if (!set) {
    set = new Set();
    subscribers.set(method, set);
  }
  set.add(callback);
  return () => {
    const s = subscribers.get(method);
    if (!s) return;
    s.delete(callback);
    if (s.size === 0) subscribers.delete(method);
  };
}
