import { describe, it, expect, beforeEach, vi } from "vitest";

interface Host {
  postMessage: (msg: string) => void;
  receiveMessage: (cb: (msg: string) => void) => void;
}

interface HostHandle {
  posted: string[];
  emit: (msg: string) => void;
}

function setupHost(): HostHandle {
  const posted: string[] = [];
  let listener: ((msg: string) => void) | null = null;
  const host: Host = {
    postMessage: (m) => {
      posted.push(m);
    },
    receiveMessage: (cb) => {
      listener = cb;
    },
  };
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  (globalThis as any).window = { infiniframe: { host } };
  return {
    posted,
    emit: (m) => {
      if (listener) listener(m);
    },
  };
}

function clearHost(): void {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  (globalThis as any).window = {};
}

beforeEach(() => {
  vi.resetModules();
  clearHost();
});

describe("rpcCall", () => {
  it("round-trips a request and resolves with result", async () => {
    const { posted, emit } = setupHost();
    const { initRpc, rpcCall } = await import("./index");
    initRpc();

    const p = rpcCall<string>("readFile", { path: "x" });

    expect(posted).toHaveLength(1);
    const req = JSON.parse(posted[0]!) as { method: string; params: unknown; id: number };
    expect(req.method).toBe("readFile");
    expect(req.params).toEqual({ path: "x" });
    expect(typeof req.id).toBe("number");

    emit(JSON.stringify({ id: req.id, result: "hello" }));
    await expect(p).resolves.toBe("hello");
  });

  it("rejects when response contains error", async () => {
    const { posted, emit } = setupHost();
    const { initRpc, rpcCall } = await import("./index");
    initRpc();

    const p = rpcCall("writeFile", { path: "x", content: "y" });
    const req = JSON.parse(posted[0]!) as { id: number };
    emit(JSON.stringify({ id: req.id, error: "disk full" }));

    await expect(p).rejects.toThrow("disk full");
  });

  it("rejects when host is not available", async () => {
    clearHost();
    const { rpcCall } = await import("./index");
    await expect(rpcCall("foo")).rejects.toThrow(/not available/);
  });

  it("silently drops responses for unknown ids", async () => {
    const { posted, emit } = setupHost();
    const { initRpc, rpcCall } = await import("./index");
    initRpc();

    const p = rpcCall("foo");
    const req = JSON.parse(posted[0]!) as { id: number };

    emit(JSON.stringify({ id: 99999, result: "stale" }));
    emit(JSON.stringify({ id: req.id, result: "ok" }));

    await expect(p).resolves.toBe("ok");
  });
});

describe("subscribe", () => {
  it("dispatches notifications to matching subscribers", async () => {
    const { emit } = setupHost();
    const { initRpc, subscribe } = await import("./index");
    initRpc();

    const received: unknown[] = [];
    subscribe("fileChanged", (p) => {
      received.push(p);
    });
    emit(JSON.stringify({ method: "fileChanged", params: { path: "/a" } }));

    expect(received).toEqual([{ path: "/a" }]);
  });

  it("ignores notifications for unsubscribed methods", async () => {
    const { emit } = setupHost();
    const { initRpc, subscribe } = await import("./index");
    initRpc();

    const received: unknown[] = [];
    subscribe("fileChanged", (p) => {
      received.push(p);
    });
    emit(JSON.stringify({ method: "otherEvent", params: { foo: "bar" } }));

    expect(received).toEqual([]);
  });

  it("unsubscribe stops further dispatches", async () => {
    const { emit } = setupHost();
    const { initRpc, subscribe } = await import("./index");
    initRpc();

    const received: unknown[] = [];
    const unsub = subscribe("fileChanged", (p) => {
      received.push(p);
    });
    emit(JSON.stringify({ method: "fileChanged", params: 1 }));
    unsub();
    emit(JSON.stringify({ method: "fileChanged", params: 2 }));

    expect(received).toEqual([1]);
  });

  it("supports multiple subscribers for the same method", async () => {
    const { emit } = setupHost();
    const { initRpc, subscribe } = await import("./index");
    initRpc();

    const a: unknown[] = [];
    const b: unknown[] = [];
    subscribe("fileChanged", (p) => {
      a.push(p);
    });
    subscribe("fileChanged", (p) => {
      b.push(p);
    });
    emit(JSON.stringify({ method: "fileChanged", params: "x" }));

    expect(a).toEqual(["x"]);
    expect(b).toEqual(["x"]);
  });

  it("silently drops malformed JSON", async () => {
    const { emit } = setupHost();
    const { initRpc, subscribe } = await import("./index");
    initRpc();

    const received: unknown[] = [];
    subscribe("fileChanged", (p) => {
      received.push(p);
    });
    emit("not-json");
    emit(JSON.stringify({ method: "fileChanged", params: "after" }));

    expect(received).toEqual(["after"]);
  });
});
