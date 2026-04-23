import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useToastStore } from "./toast.tsx";

describe("toastStore", () => {
  beforeEach(() => {
    useToastStore.setState({ toasts: [] });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("push adds a toast with a unique id and a sticker", () => {
    useToastStore.getState().push({ variant: "success", message: "Saved" });
    useToastStore.getState().push({ variant: "info", message: "Later" });
    const toasts = useToastStore.getState().toasts;
    expect(toasts).toHaveLength(2);
    expect(toasts[0]!.id).not.toBe(toasts[1]!.id);
    expect(toasts[0]!.sticker).toBeDefined();
    expect(toasts[0]!.message).toBe("Saved");
  });

  it("preserves push-order as the toasts array", () => {
    useToastStore.getState().push({ variant: "info", message: "first" });
    useToastStore.getState().push({ variant: "info", message: "second" });
    useToastStore.getState().push({ variant: "info", message: "third" });
    expect(useToastStore.getState().toasts.map((t) => t.message)).toEqual([
      "first",
      "second",
      "third",
    ]);
  });

  it("dismiss removes the toast with the matching id", () => {
    useToastStore.getState().push({ variant: "info", message: "A" });
    useToastStore.getState().push({ variant: "info", message: "B" });
    const { toasts } = useToastStore.getState();
    useToastStore.getState().dismiss(toasts[0]!.id);
    expect(useToastStore.getState().toasts.map((t) => t.message)).toEqual(["B"]);
  });

  it("dismiss returns the same toasts reference for an unknown id", () => {
    useToastStore.getState().push({ variant: "info", message: "A" });
    const before = useToastStore.getState().toasts;
    useToastStore.getState().dismiss("toast-does-not-exist");
    expect(useToastStore.getState().toasts).toBe(before);
  });

  it("auto-dismisses each toast after 8s", () => {
    vi.useFakeTimers();
    useToastStore.getState().push({ variant: "error", message: "Oops" });
    expect(useToastStore.getState().toasts).toHaveLength(1);
    vi.advanceTimersByTime(7_999);
    expect(useToastStore.getState().toasts).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });

  it("each toast's auto-dismiss fires independently", () => {
    vi.useFakeTimers();
    useToastStore.getState().push({ variant: "info", message: "first" });
    vi.advanceTimersByTime(4_000);
    useToastStore.getState().push({ variant: "info", message: "second" });
    vi.advanceTimersByTime(4_001);
    // First toast is gone (~8s elapsed), second is still visible (~4s elapsed).
    expect(useToastStore.getState().toasts.map((t) => t.message)).toEqual(["second"]);
    vi.advanceTimersByTime(4_000);
    expect(useToastStore.getState().toasts).toHaveLength(0);
  });
});
