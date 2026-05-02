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
    const input = screen.getByRole("textbox", { name: "test" });
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

  it("calls onCommit on Enter via blur", () => {
    const onCommit = vi.fn();
    render(createElement(CommitInput, { value: "old", onCommit, "aria-label": "test" }));
    const input = screen.getByRole("textbox", { name: "test" });
    // Focus first so blur() actually fires a blur event in jsdom.
    input.focus();
    fireEvent.change(input, { target: { value: "updated" } });
    fireEvent.keyDown(input, { key: "Enter" });
    // Enter triggers currentElement.blur() which fires the onBlur handler.
    expect(onCommit).toHaveBeenCalledWith("updated");
  });

  it("re-keys when external value prop changes", () => {
    const onCommit = vi.fn();
    const { rerender } = render(
      createElement(CommitInput, { value: "v1", onCommit, "aria-label": "test" }),
    );
    const input1 = screen.getByRole("textbox", { name: "test" });
    expect(input1.value).toBe("v1");

    rerender(createElement(CommitInput, { value: "v2", onCommit, "aria-label": "test" }));
    const input2 = screen.getByRole("textbox", { name: "test" });
    expect(input2.value).toBe("v2");
  });
});
