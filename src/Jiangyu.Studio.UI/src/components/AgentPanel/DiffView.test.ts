// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { render, cleanup } from "@testing-library/react";

vi.mock("./AgentPanel.module.css", () => ({
  // The proxy lets us assert against className tokens by their CSS-module
  // identifier without coupling tests to the codegen output.
  default: new Proxy({}, { get: (_, key) => key }),
}));

import { DiffStatsBadge, DiffView } from "./DiffView";
import { computeLineDiff, diffStats } from "@lib/agent/diff";

afterEach(() => cleanup());

describe("DiffView", () => {
  it("renders one row per diff line with the correct marker", () => {
    const { container } = render(
      createElement(DiffView, {
        diff: { type: "diff", path: "x.kdl", oldText: "old", newText: "new" },
      }),
    );
    // Whole-line replacement: old removed, new added. No context.
    expect(container.querySelector(".diffLineRemoved")).not.toBeNull();
    expect(container.querySelector(".diffLineAdded")).not.toBeNull();
    expect(container.querySelector(".diffLineContext")).toBeNull();
  });

  it("treats absent oldText as a brand-new file (all lines added)", () => {
    const { container } = render(
      createElement(DiffView, {
        diff: { type: "diff", path: "fresh.kdl", newText: "alpha\nbeta" },
      }),
    );
    expect(container.querySelectorAll(".diffLineAdded").length).toBe(2);
    expect(container.querySelector(".diffLineRemoved")).toBeNull();
  });

  it("clamps very long diffs and surfaces the hidden-count gap", () => {
    // Build a 250-line all-context diff. With MaxLines=200 it should clamp
    // to head 100 + tail 50 + 1 gap row.
    const lines = Array.from({ length: 250 }, (_, i) => `line ${i}`).join("\n");
    const { container } = render(
      createElement(DiffView, {
        diff: { type: "diff", path: "huge.txt", oldText: lines, newText: lines },
      }),
    );
    const gap = container.querySelector(".diffGap");
    expect(gap).not.toBeNull();
    expect(gap?.textContent).toContain("100 unchanged lines hidden");
    // 100 head + 50 tail = 150 line rows under .diffBody (plus the gap).
    const rows = container.querySelectorAll(".diffLineContext");
    expect(rows.length).toBe(150);
  });
});

describe("DiffStatsBadge", () => {
  it("renders +N and −N counts", () => {
    const stats = diffStats(computeLineDiff("alpha\nbeta\ngamma", "alpha\nBETA\ndelta\ngamma"));
    const { container } = render(createElement(DiffStatsBadge, { stats }));
    expect(container.textContent).toContain("+2");
    expect(container.textContent).toContain("−1");
  });

  it("hides additions or removals when their count is zero", () => {
    const noChange = render(createElement(DiffStatsBadge, { stats: { added: 0, removed: 0 } }));
    // Empty stats → component returns null and renders nothing.
    expect(noChange.container.firstChild).toBeNull();

    const onlyAdds = render(createElement(DiffStatsBadge, { stats: { added: 3, removed: 0 } }));
    expect(onlyAdds.container.textContent).toBe("+3");

    const onlyRemoves = render(createElement(DiffStatsBadge, { stats: { added: 0, removed: 5 } }));
    expect(onlyRemoves.container.textContent).toBe("−5");
  });
});
