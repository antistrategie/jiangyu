// @vitest-environment jsdom
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { useState, type RefObject } from "react";
import { renderHook, act, cleanup } from "@testing-library/react";
import { useAnchorPosition } from "./useAnchorPosition";

beforeEach(() => {
  vi.stubGlobal("innerHeight", 768);
  vi.stubGlobal("innerWidth", 1024);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

// jsdom returns zeros for getBoundingClientRect by default; stub it on the
// HTMLElement prototype so the hook reads predictable coordinates.
function stubBoundingRect(top: number, left: number, width: number, height: number) {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue(
    new DOMRect(left, top, width, height),
  );
}

interface HookProps {
  readonly open: boolean;
}

function useProbe({ open }: HookProps) {
  // The hook only reads anchorRef.current under useLayoutEffect / event
  // handlers, so a stable ref-shaped object initialised once with a detached
  // DOM node satisfies the hook without touching React refs.
  const [anchorRef] = useState<RefObject<HTMLElement | null>>(() => ({
    current: document.createElement("div"),
  }));
  return { position: useAnchorPosition(anchorRef, open) };
}

describe("useAnchorPosition", () => {
  it("returns null while closed", () => {
    stubBoundingRect(10, 20, 100, 30);
    const { result } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: false },
    });
    expect(result.current.position).toBeNull();
  });

  it("opens below the anchor when there is room", () => {
    stubBoundingRect(50, 80, 200, 40);
    const { result } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: true },
    });
    expect(result.current.position).toEqual({
      top: 90,
      bottom: "auto",
      left: 80,
      width: 200,
      maxHeight: 240,
    });
  });

  it("updates on window scroll", () => {
    stubBoundingRect(50, 80, 200, 40);
    const { result } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: true },
    });
    expect(result.current.position).toMatchObject({ top: 90, left: 80, width: 200 });

    stubBoundingRect(120, 80, 200, 40);
    act(() => {
      window.dispatchEvent(new Event("scroll"));
    });
    expect(result.current.position).toMatchObject({ top: 160, left: 80, width: 200 });
  });

  it("updates on window resize", () => {
    stubBoundingRect(50, 80, 200, 40);
    const { result } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: true },
    });

    stubBoundingRect(50, 80, 300, 40);
    act(() => {
      window.dispatchEvent(new Event("resize"));
    });
    expect(result.current.position).toMatchObject({ top: 90, left: 80, width: 300 });
  });

  it("opens above a field at the bottom of the window", () => {
    stubBoundingRect(710, 80, 200, 30);
    const { result } = renderHook(() => useProbe({ open: true }));

    expect(result.current.position).toEqual({
      top: "auto",
      bottom: 58,
      left: 80,
      width: 200,
      maxHeight: 240,
    });
  });

  it("limits a menu to the available space in a short window", () => {
    vi.stubGlobal("innerHeight", 200);
    stubBoundingRect(110, 80, 200, 30);
    const { result } = renderHook(() => useProbe({ open: true }));

    expect(result.current.position).toMatchObject({ top: "auto", bottom: 90, maxHeight: 102 });
  });

  it("keeps a short menu below when there is more space there", () => {
    vi.stubGlobal("innerHeight", 200);
    stubBoundingRect(30, 80, 200, 30);
    const { result } = renderHook(() => useProbe({ open: true }));

    expect(result.current.position).toMatchObject({ top: 60, bottom: "auto", maxHeight: 132 });
  });

  it("repositions when the window height changes without moving the anchor", () => {
    stubBoundingRect(400, 80, 200, 30);
    const { result } = renderHook(() => useProbe({ open: true }));
    expect(result.current.position?.top).toBe(430);

    vi.stubGlobal("innerHeight", 500);
    act(() => {
      window.dispatchEvent(new Event("resize"));
    });

    expect(result.current.position).toMatchObject({ top: "auto", bottom: 100, maxHeight: 240 });
  });

  it("keeps the menu inside the right edge of the viewport", () => {
    stubBoundingRect(50, 960, 200, 30);
    const { result } = renderHook(() => useProbe({ open: true }));

    expect(result.current.position).toMatchObject({ left: 816, width: 200 });
  });

  it("limits menus wider than the viewport", () => {
    stubBoundingRect(50, -10, 1200, 30);
    const { result } = renderHook(() => useProbe({ open: true }));

    expect(result.current.position).toMatchObject({ left: 8, width: 1008 });
  });

  it("keeps the same position object across no-op scrolls so consumers don't re-render", () => {
    stubBoundingRect(50, 80, 200, 40);
    const { result } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: true },
    });
    const initial = result.current.position;
    act(() => {
      window.dispatchEvent(new Event("scroll"));
      window.dispatchEvent(new Event("scroll"));
    });
    expect(result.current.position).toBe(initial);
  });

  it("returns null when transitioning from open to closed", () => {
    stubBoundingRect(50, 80, 200, 40);
    const { result, rerender } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: true },
    });
    expect(result.current.position).not.toBeNull();
    rerender({ open: false });
    expect(result.current.position).toBeNull();
  });
});
