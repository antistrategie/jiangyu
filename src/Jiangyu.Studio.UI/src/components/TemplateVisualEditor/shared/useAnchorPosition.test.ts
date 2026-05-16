// @vitest-environment jsdom
import { describe, it, expect, afterEach } from "vitest";
import { useState, type RefObject } from "react";
import { renderHook, act, cleanup } from "@testing-library/react";
import { useAnchorPosition } from "./useAnchorPosition";

afterEach(cleanup);

// jsdom returns zeros for getBoundingClientRect by default; stub it on the
// HTMLElement prototype so the hook reads predictable coordinates.
function stubBoundingRect(top: number, left: number, width: number, height: number) {
  HTMLElement.prototype.getBoundingClientRect = function () {
    return {
      top,
      left,
      bottom: top + height,
      right: left + width,
      width,
      height,
      x: left,
      y: top,
      toJSON() {
        return {};
      },
    };
  };
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

  it("returns viewport-bottom-anchored coords when open", () => {
    stubBoundingRect(50, 80, 200, 40);
    const { result } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: true },
    });
    expect(result.current.position).toEqual({ top: 90, left: 80, width: 200 });
  });

  it("updates on window scroll", () => {
    stubBoundingRect(50, 80, 200, 40);
    const { result } = renderHook((props: HookProps) => useProbe(props), {
      initialProps: { open: true },
    });
    expect(result.current.position).toEqual({ top: 90, left: 80, width: 200 });

    stubBoundingRect(120, 80, 200, 40);
    act(() => {
      window.dispatchEvent(new Event("scroll"));
    });
    expect(result.current.position).toEqual({ top: 160, left: 80, width: 200 });
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
    expect(result.current.position).toEqual({ top: 90, left: 80, width: 300 });
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
