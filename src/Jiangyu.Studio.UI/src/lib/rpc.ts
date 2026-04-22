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
  readonly error?: string;
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

type PendingRequest = {
  readonly resolve: (value: unknown) => void;
  readonly reject: (reason: Error) => void;
};

type NotificationCallback = (params: unknown) => void;

interface InfiniFrameHost {
  postMessage: (msg: string) => void;
  receiveMessage: (callback: (msg: string) => void) => void;
}

let nextId = 1;
const pending = new Map<number, PendingRequest>();
const subscribers = new Map<string, Set<NotificationCallback>>();

function getHost(): InfiniFrameHost | undefined {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const win = window as any;
  // WebKitGTK (Linux/macOS)
  if (win.infiniframe?.host) return win.infiniframe.host as InfiniFrameHost;
  // WebView2 (Windows)
  if (win.chrome?.webview) {
    const wv = win.chrome.webview;
    return {
      postMessage: (msg: string) => wv.postMessage(msg),
      receiveMessage: (cb: (msg: string) => void) =>
        wv.addEventListener("message", (e: MessageEvent<string>) => cb(e.data)),
    };
  }
  return undefined;
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
      if (msg.error !== undefined && msg.error !== null) {
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

/**
 * Subscribe to a notification method pushed from the host.
 * Returns an unsubscribe function.
 */
export function subscribe<T = unknown>(method: string, callback: (params: T) => void): () => void {
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
