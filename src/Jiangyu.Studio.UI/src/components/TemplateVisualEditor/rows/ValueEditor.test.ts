// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement, useState } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";

afterEach(cleanup);
import type { EditorValue } from "../types";

vi.mock("../TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

vi.mock("@lib/rpc", () => ({
  rpcCall: vi.fn(() => Promise.resolve({ members: [], instances: [], types: [] })),
}));

// Mock the virtualiser for SuggestionCombobox (used by Enum/Ref editors).
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

import { ValueEditor } from "./ValueEditor";

// Controlled wrapper so onChange is reflected in re-renders.
function Controlled(props: { initial: EditorValue; onChange?: (v: EditorValue) => void }) {
  const [value, setValue] = useState(props.initial);
  return createElement(ValueEditor, {
    value,
    onChange: (v: EditorValue) => {
      setValue(v);
      props.onChange?.(v);
    },
  });
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("ValueEditor", () => {
  it("Boolean renders checkbox, toggles on click", () => {
    const onChange = vi.fn();
    render(createElement(Controlled, { initial: { kind: "Boolean", boolean: false }, onChange }));
    const checkbox = screen.getByRole("checkbox") as HTMLInputElement;
    expect(checkbox.checked).toBe(false);
    fireEvent.click(checkbox);
    expect(onChange).toHaveBeenCalledWith({ kind: "Boolean", boolean: true });
  });

  it("Int32 renders number input, commits on blur", () => {
    const onChange = vi.fn();
    render(createElement(Controlled, { initial: { kind: "Int32", int32: 42 }, onChange }));
    const input = screen.getByRole("spinbutton") as HTMLInputElement;
    expect(input.value).toBe("42");
    fireEvent.change(input, { target: { value: "99" } });
    fireEvent.blur(input);
    expect(onChange).toHaveBeenCalledWith({ kind: "Int32", int32: 99 });
  });

  it("String renders text input", () => {
    const onChange = vi.fn();
    render(createElement(Controlled, { initial: { kind: "String", string: "hello" }, onChange }));
    const input = screen.getByRole("textbox") as HTMLInputElement;
    expect(input.value).toBe("hello");
  });

  it("Enum renders SuggestionCombobox (EnumValueEditor)", () => {
    render(
      createElement(ValueEditor, {
        value: { kind: "Enum", enumValue: "Sword" },
        onChange: vi.fn(),
        member: {
          name: "slot",
          typeName: "ItemSlot",
          isWritable: true,
          isInherited: false,
          enumTypeName: "ItemSlot",
        },
      }),
    );
    // The enum value editor renders a text input for the combobox.
    const input = screen.getByPlaceholderText("ItemSlot value") as HTMLInputElement;
    expect(input.value).toBe("Sword");
  });

  it("TemplateReference renders RefValueEditor", () => {
    render(
      createElement(ValueEditor, {
        value: { kind: "TemplateReference", referenceId: "archer_01" },
        onChange: vi.fn(),
        member: {
          name: "target",
          typeName: "UnitTemplate",
          isWritable: true,
          isInherited: false,
          referenceTypeName: "UnitTemplate",
        },
      }),
    );
    // The ref value editor renders a combobox with the id placeholder.
    const input = screen.getByPlaceholderText("UnitTemplate id") as HTMLInputElement;
    expect(input.value).toBe("archer_01");
  });
});
