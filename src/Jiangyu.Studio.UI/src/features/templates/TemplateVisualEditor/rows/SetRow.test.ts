// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, waitFor, act, cleanup } from "@testing-library/react";

afterEach(cleanup);

vi.mock("../TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

// Mock the virtualiser — jsdom has no layout engine so measurements return 0.
vi.mock("@tanstack/react-virtual", () => ({
  useVirtualizer: (opts: { count: number }) => ({
    getTotalSize: () => opts.count * 28,
    getVirtualItems: () =>
      Array.from({ length: opts.count }, (_, i) => ({
        index: i,
        start: i * 28,
        size: 28,
        key: i,
      })),
  }),
}));

// Quiet the editor's RPC paths. CompositeEditor calls useTemplateMembers
// when expanded and the prototype-types fetch on first render of a
// from=-supporting type; both go through rpcCall.
vi.mock("@shared/rpc", () => ({
  rpcCall: vi.fn(() => Promise.resolve({ members: [], instances: [], types: [] })),
}));

vi.mock("../shared/rpcHelpers", async () => {
  const actual: object = await vi.importActual("../shared/rpcHelpers");
  return {
    ...actual,
    templatesPrototypeSupportedTypes: vi.fn(() => Promise.resolve(new Set<string>())),
    templatesPrototypeCandidates: vi.fn(() => Promise.resolve([])),
  };
});

import { CompositeEditor, HandlerSubtypePicker } from "./SetRow";
import { CompositeCollapseContext, type CompositeCollapseControl } from "../store";
import type { EditorValue } from "../types";

// HandlerSubtypePicker is the polymorphic-handler "must pick from list"
// surface. Its bug history: the original implementation passed `value=""`
// hardcoded to the underlying SuggestionCombobox and treated every
// onChange as a commit, so typing the first letter of a handler type
// committed that letter as the chosen type. The fix moves typed-text
// state local to this component and commits only via SuggestionCombobox's
// onCommit callback.

describe("HandlerSubtypePicker", () => {
  const subtypeChoices = ["AddSkill", "AddPerk", "ApplyEffect"];

  it("does not commit when the user types a single character", async () => {
    const onPick = vi.fn();
    render(
      createElement(HandlerSubtypePicker, {
        subtypeChoices,
        onPick,
      }),
    );
    const input = screen.getByPlaceholderText<HTMLInputElement>("Pick handler type…");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("AddSkill")).toBeDefined());
    act(() => {
      fireEvent.change(input, { target: { value: "a" } });
    });
    expect(onPick).not.toHaveBeenCalled();
    // Typed text is reflected in the input (the previous bug rendered
    // the input as empty because value was hardcoded).
    expect(input.value).toBe("a");
  });

  it("commits when the user clicks a suggestion", async () => {
    const onPick = vi.fn();
    render(
      createElement(HandlerSubtypePicker, {
        subtypeChoices,
        onPick,
      }),
    );
    const input = screen.getByPlaceholderText("Pick handler type…");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("AddSkill")).toBeDefined());
    act(() => {
      fireEvent.click(screen.getByText("AddSkill"));
    });
    expect(onPick).toHaveBeenCalledWith("AddSkill");
    expect(onPick).toHaveBeenCalledTimes(1);
  });

  it("commits the first filtered match on Enter", async () => {
    const onPick = vi.fn();
    render(
      createElement(HandlerSubtypePicker, {
        subtypeChoices,
        onPick,
      }),
    );
    const input = screen.getByPlaceholderText("Pick handler type…");
    act(() => {
      fireEvent.focus(input);
    });
    await waitFor(() => expect(screen.getByText("AddSkill")).toBeDefined());
    // Filter down to handlers starting with "ap".
    act(() => {
      fireEvent.change(input, { target: { value: "ap" } });
    });
    expect(onPick).not.toHaveBeenCalled();
    act(() => {
      fireEvent.keyDown(input, { key: "Enter" });
    });
    expect(onPick).toHaveBeenCalledWith("ApplyEffect");
  });
});

// --- CompositeEditor collapse behaviour ---
//
// Lazy-mounting the body is the load-bearing perf fix for huge clones
// like voicelines.kdl (374 directives, each carrying a Sound composite).
// These tests pin the default rules (collapsed when populated, expanded
// when empty), the toggle plumbing through the header (chevron + label +
// chip + count are one click target), and the context-backed persistence
// path so a CompositeCollapseContext explicitly overrides the default.

function populatedComposite(): EditorValue {
  return {
    kind: "Composite",
    compositeType: "Sound",
    compositeDirectives: [
      {
        op: "Set",
        fieldPath: "name",
        value: { kind: "String", string: "Attack" },
        _uiId: "inner",
      },
    ],
  };
}

function emptyComposite(): EditorValue {
  return { kind: "Composite", compositeType: "Sound", compositeDirectives: [] };
}

describe("CompositeEditor collapse", () => {
  it("defaults collapsed when the composite has directives", () => {
    render(
      createElement(CompositeEditor, {
        value: populatedComposite(),
        onChange: vi.fn(),
      }),
    );
    // Header is rendered with the collapsed title and the field-count hint.
    expect(screen.getByTitle("Expand composite")).toBeDefined();
    expect(screen.getByText("1 field")).toBeDefined();
  });

  it("defaults expanded when the composite has no directives", () => {
    render(
      createElement(CompositeEditor, {
        value: emptyComposite(),
        onChange: vi.fn(),
      }),
    );
    expect(screen.getByTitle("Collapse composite")).toBeDefined();
    // Field count is suppressed when expanded.
    expect(screen.queryByText(/field$/)).toBeNull();
  });

  it("clicking the header toggles collapse", () => {
    render(
      createElement(CompositeEditor, {
        value: populatedComposite(),
        onChange: vi.fn(),
      }),
    );
    const header = screen.getByTitle("Expand composite");
    act(() => {
      fireEvent.click(header);
    });
    expect(screen.getByTitle("Collapse composite")).toBeDefined();
  });

  it("clicking the kind label toggles via bubble (whole header is the hit target)", () => {
    render(
      createElement(CompositeEditor, {
        value: populatedComposite(),
        onChange: vi.fn(),
      }),
    );
    const label = screen.getByText("composite");
    act(() => {
      fireEvent.click(label);
    });
    expect(screen.getByTitle("Collapse composite")).toBeDefined();
  });

  it("clicking the type chip toggles via bubble", () => {
    render(
      createElement(CompositeEditor, {
        value: populatedComposite(),
        onChange: vi.fn(),
      }),
    );
    const chip = screen.getByText("Sound");
    act(() => {
      fireEvent.click(chip);
    });
    expect(screen.getByTitle("Collapse composite")).toBeDefined();
  });

  it("keyboard Enter on the header toggles collapse", () => {
    render(
      createElement(CompositeEditor, {
        value: populatedComposite(),
        onChange: vi.fn(),
      }),
    );
    const header = screen.getByTitle("Expand composite");
    act(() => {
      fireEvent.keyDown(header, { key: "Enter" });
    });
    expect(screen.getByTitle("Collapse composite")).toBeDefined();
  });

  it("CompositeCollapseContext overrides the content default", () => {
    // Persisted state says "expanded" even though directives.length > 0
    // would default to collapsed. The header should render the
    // expanded title.
    const control: CompositeCollapseControl = {
      resolveState: (uiId) => (uiId === "outer" ? false : undefined),
      toggle: vi.fn(),
    };
    render(
      createElement(
        CompositeCollapseContext,
        { value: control },
        createElement(CompositeEditor, {
          value: populatedComposite(),
          onChange: vi.fn(),
          directiveUiId: "outer",
        }),
      ),
    );
    expect(screen.getByTitle("Collapse composite")).toBeDefined();
  });

  it("CompositeCollapseContext.toggle fires with the directiveUiId and target state", () => {
    const toggle = vi.fn();
    const control: CompositeCollapseControl = {
      resolveState: () => undefined,
      toggle,
    };
    render(
      createElement(
        CompositeCollapseContext,
        { value: control },
        createElement(CompositeEditor, {
          value: populatedComposite(),
          onChange: vi.fn(),
          directiveUiId: "outer",
        }),
      ),
    );
    const header = screen.getByTitle("Expand composite");
    act(() => {
      fireEvent.click(header);
    });
    // Default for populatedComposite is collapsed=true, click should
    // request the inverted state.
    expect(toggle).toHaveBeenCalledWith("outer", false);
  });
});
