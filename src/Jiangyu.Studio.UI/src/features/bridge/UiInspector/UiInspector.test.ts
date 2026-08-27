// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { UiDump } from "@shared/rpc";

const bridgeUiCapture = vi.fn();
vi.mock("@features/bridge/bridge", () => ({
  bridgeUiCapture: () => bridgeUiCapture() as Promise<UiDump | null>,
}));

vi.mock("@features/bridge/useBridgeStatus", () => ({
  useBridgeStatus: () => ({
    status: { enabled: true, connected: true },
    setStatus: vi.fn(),
  }),
}));

vi.mock("@shared/toast", () => ({ useToastPush: () => vi.fn() }));

import { UiInspector } from "./UiInspector";

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

/** Clicks the toolbar Capture button and waits for the dump to render. */
async function capture(dump: UiDump | null): Promise<void> {
  bridgeUiCapture.mockResolvedValue(dump);
  render(createElement(UiInspector));
  fireEvent.click(screen.getAllByRole("button", { name: "Capture" })[0]!);
  await waitFor(() => {
    expect(bridgeUiCapture).toHaveBeenCalled();
  });
}

describe("UiInspector", () => {
  // The bridge serialises with WhenWritingNull: a node's null fields arrive absent,
  // not null. A strict `!== null` guard lets `undefined` through and the tree render
  // throws on the first node that has no name or text.
  it("renders a dump whose null fields the bridge omitted", async () => {
    await capture({
      nodeCount: 2,
      truncated: false,
      screenTree: {
        type: "VisualElement",
        children: [{ type: "Label", text: "  Deploy   squad\n" }],
      },
    });

    await waitFor(() => {
      expect(screen.getByText("VisualElement")).toBeTruthy();
    });
    expect(screen.getByText("Label")).toBeTruthy();
    // truncate() collapses the whitespace run.
    expect(screen.getByText(/Deploy squad/)).toBeTruthy();
    expect(screen.getByText("(no screen)")).toBeTruthy();
    expect(screen.getByText("2 nodes")).toBeTruthy();
  });

  it("reports an empty capture when both trees are omitted", async () => {
    await capture({ nodeCount: 0, truncated: false });

    await waitFor(() => {
      expect(screen.getByText("No UI tree captured.")).toBeTruthy();
    });
  });
});
