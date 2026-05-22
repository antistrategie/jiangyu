// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";

afterEach(cleanup);

vi.mock("../TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

import { CommitInput } from "./CommitInput";

describe("CommitInput", () => {
  it("renders with the given value as defaultValue", () => {
    render(createElement(CommitInput, { value: "hello", onCommit: vi.fn(), "aria-label": "test" }));
    const input = screen.getByRole<HTMLInputElement>("textbox", { name: "test" });
    expect(input.value).toBe("hello");
  });

  it("calls onCommit on blur when value changed", () => {
    const onCommit = vi.fn();
    render(createElement(CommitInput, { value: "old", onCommit, "aria-label": "test" }));
    const input = screen.getByRole("textbox", { name: "test" });
    fireEvent.change(input, { target: { value: "new" } });
    fireEvent.blur(input);
    expect(onCommit).toHaveBeenCalledWith("new");
  });

  it("does NOT call onCommit on blur when value unchanged", () => {
    const onCommit = vi.fn();
    render(createElement(CommitInput, { value: "same", onCommit, "aria-label": "test" }));
    const input = screen.getByRole("textbox", { name: "test" });
    fireEvent.blur(input);
    expect(onCommit).not.toHaveBeenCalled();
  });

  it("Enter on a text input inserts a newline and commits", () => {
    // Pressing Enter inside a plain text CommitInput inserts a newline
    // at the caret and commits the new value, rather than blurring. The
    // String value editor watches for newlines and re-renders as a
    // textarea so the modder gets multi-line editing without an
    // explicit toggle.
    const onCommit = vi.fn();
    render(createElement(CommitInput, { value: "old", onCommit, "aria-label": "test" }));
    const input = screen.getByRole<HTMLInputElement>("textbox", { name: "test" });
    input.focus();
    fireEvent.change(input, { target: { value: "updated" } });
    // Place caret at end so the spliced newline lands at the tail.
    input.setSelectionRange(input.value.length, input.value.length);
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith("updated\n");
  });

  it("Enter on a number input still blur-commits (no newline)", () => {
    // Numeric / non-text inputs keep the original "Enter commits"
    // behaviour because newlines aren't meaningful for them.
    const onCommit = vi.fn();
    render(
      createElement(CommitInput, {
        value: "5",
        onCommit,
        type: "number",
        "aria-label": "test",
      }),
    );
    const input = screen.getByRole<HTMLInputElement>("spinbutton", { name: "test" });
    input.focus();
    fireEvent.change(input, { target: { value: "9" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onCommit).toHaveBeenCalledWith("9");
  });

  it("re-keys when external value prop changes", () => {
    const onCommit = vi.fn();
    const { rerender } = render(
      createElement(CommitInput, { value: "v1", onCommit, "aria-label": "test" }),
    );
    const input1 = screen.getByRole<HTMLInputElement>("textbox", { name: "test" });
    expect(input1.value).toBe("v1");

    rerender(createElement(CommitInput, { value: "v2", onCommit, "aria-label": "test" }));
    const input2 = screen.getByRole<HTMLInputElement>("textbox", { name: "test" });
    expect(input2.value).toBe("v2");
  });
});
