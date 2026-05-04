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

import { HandlerSubtypePicker } from "./SetRow";

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
