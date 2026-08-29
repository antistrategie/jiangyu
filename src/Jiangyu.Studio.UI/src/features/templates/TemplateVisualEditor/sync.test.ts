// @vitest-environment jsdom
//
// Round-trip behaviour of the editor's content sync: what it sends back to
// the buffer, when it sends it, and which of two competing writes wins. The
// editor parses and serialises over an RPC, so every write lands at least a
// round trip after the edit that produced it — long enough for the modder to
// have switched to source mode, switched tabs, or for an external change to
// have reloaded underneath.
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup, act } from "@testing-library/react";

vi.mock("./TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

// Incidental catalogue lookups from the rendered cards (template types,
// instance suggestions, vanilla field values) hang rather than reject, so
// they never race the assertions.
vi.mock("@shared/rpc", () => ({
  rpcCall: vi.fn(() => new Promise(() => {})),
  subscribe: vi.fn(() => () => {}),
}));

vi.mock("./shared/rpcHelpers", async (orig) => {
  const actual = await orig<typeof import("./shared/rpcHelpers")>();
  return { ...actual, templatesParse: vi.fn(), templatesSerialise: vi.fn() };
});

import { templatesParse, templatesSerialise } from "./shared/rpcHelpers";
import type { EditorDocument } from "./types";
import { TemplateVisualEditor } from "./TemplateVisualEditor";

const parse = vi.mocked(templatesParse);
const serialise = vi.mocked(templatesSerialise);

/** A promise plus the handle to settle it, so a test can resolve two
 *  in-flight RPCs in whatever order it wants to exercise. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => (resolve = r));
  return { promise, resolve };
}

function doc(partial: Partial<EditorDocument> = {}): EditorDocument {
  return { nodes: [], errors: [], ...partial };
}

/** Settle the microtask queue the RPC continuations are sitting on. */
async function settle() {
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
}

function renderEditor(props: {
  content: string;
  filePath?: string;
  onChange?: (content: string, expectedPrevious?: string) => boolean;
}) {
  const onChange = props.onChange ?? vi.fn(() => true);
  const view = render(
    createElement(TemplateVisualEditor, {
      content: props.content,
      filePath: props.filePath ?? "/mod/templates/units.kdl",
      onChange,
    }),
  );
  return { ...view, onChange };
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  parse.mockReset();
  serialise.mockReset();
  parse.mockResolvedValue(doc());
  serialise.mockResolvedValue({ text: "serialised" });
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

/** Make an edit through the UI so the editor schedules a serialise. */
function addNode() {
  fireEvent.click(screen.getByText("Add Create"));
}

describe("TemplateVisualEditor content sync", () => {
  it("carries trailing comments across the round trip", async () => {
    parse.mockResolvedValue(doc({ trailingComments: ["// keep me"] }));
    renderEditor({ content: "patch {}" });
    await settle();

    addNode();
    await act(async () => {
      vi.advanceTimersByTime(200);
      await Promise.resolve();
    });

    expect(serialise).toHaveBeenCalledTimes(1);
    expect(serialise.mock.calls[0]?.[0].trailingComments).toEqual(["// keep me"]);
  });

  it("writes back with the buffer text the edit was based on", async () => {
    const { onChange } = renderEditor({ content: "patch {}" });
    await settle();

    addNode();
    await act(async () => {
      vi.advanceTimersByTime(200);
      await Promise.resolve();
      await Promise.resolve();
    });

    expect(onChange).toHaveBeenCalledWith("serialised", "patch {}");
  });

  it("flushes a debounced edit on unmount rather than after it", async () => {
    const { unmount } = renderEditor({ content: "patch {}" });
    await settle();

    addNode();
    expect(serialise).not.toHaveBeenCalled();

    // A switch to source mode unmounts the editor mid-debounce. The edit has
    // to reach the buffer now, not 150ms into the modder's typing.
    unmount();
    expect(serialise).toHaveBeenCalledTimes(1);
  });

  it("flushes a debounced edit when the tab switches away", async () => {
    const { rerender, onChange } = renderEditor({ content: "patch {}" });
    await settle();

    addNode();
    parse.mockResolvedValue(doc());
    rerender(
      createElement(TemplateVisualEditor, {
        content: "other file",
        filePath: "/mod/templates/other.kdl",
        onChange,
      }),
    );

    expect(serialise).toHaveBeenCalledTimes(1);
  });

  it("drops a debounced edit when the buffer changes underneath it", async () => {
    const { rerender, onChange } = renderEditor({ content: "patch {}" });
    await settle();

    addNode();
    // Same file, new text: an external change reloaded into the buffer. The
    // scheduled edit holds the pre-change tree and must not be written over
    // it.
    rerender(
      createElement(TemplateVisualEditor, {
        content: "changed on disk",
        filePath: "/mod/templates/units.kdl",
        onChange,
      }),
    );
    await act(async () => {
      vi.advanceTimersByTime(200);
      await Promise.resolve();
    });

    expect(serialise).not.toHaveBeenCalled();
    expect(parse).toHaveBeenLastCalledWith("changed on disk");
  });

  it("ignores a parse response that a newer one has overtaken", async () => {
    const first = deferred<EditorDocument>();
    const second = deferred<EditorDocument>();
    parse.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    const { rerender, onChange } = renderEditor({ content: "first" });
    rerender(
      createElement(TemplateVisualEditor, {
        content: "second",
        filePath: "/mod/templates/units.kdl",
        onChange,
      }),
    );

    // The host answers the second parse first; the stale first response must
    // not load a tree the buffer no longer holds.
    second.resolve(doc({ nodes: [{ kind: "Create", templateType: "Second", directives: [] }] }));
    await settle();
    first.resolve(doc({ nodes: [{ kind: "Create", templateType: "First", directives: [] }] }));
    await settle();

    expect(screen.queryByDisplayValue("Second")).not.toBeNull();
    expect(screen.queryByDisplayValue("First")).toBeNull();
  });
});
