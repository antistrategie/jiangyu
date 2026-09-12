// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement, useState } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";

afterEach(cleanup);
import type { EditorValue } from "../types";

vi.mock("../TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

vi.mock("@shared/rpc", () => ({
  rpcCall: vi.fn(() => Promise.resolve({ members: [], suggestions: [] })),
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
import { rpcCall } from "@shared/rpc";
import {
  invalidateProjectClonesCache,
  invalidateProjectAdditionsCache,
  templateTypesCache,
} from "../shared/rpcHelpers";

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
  it.each([
    { typeName: "Sprite" },
    { typeName: "List<Sprite>", isCollection: true, elementTypeName: "Sprite" },
  ])("uses the asset element type for $typeName", async (shape) => {
    invalidateProjectAdditionsCache();
    vi.mocked(rpcCall).mockImplementation((method) =>
      Promise.resolve(
        method === "assetsProjectAdditions"
          ? {
              additions: [
                {
                  name: "remolding/support",
                  file: "assets/additions/sprites/remolding/support.png",
                },
              ],
            }
          : [],
      ),
    );
    const onChange = vi.fn();
    render(
      createElement(ValueEditor, {
        value: { kind: "AssetReference", assetName: "" },
        onChange,
        member: { name: "Icons", isWritable: true, isInherited: false, ...shape },
      }),
    );
    fireEvent.focus(screen.getByPlaceholderText("path/to/asset"));
    fireEvent.click(await screen.findByRole("button", { name: /remolding\/support\s*addition/ }));
    expect(rpcCall).toHaveBeenCalledWith("assetsProjectAdditions", { unityType: "Sprite" });
    expect(rpcCall).toHaveBeenCalledWith("assetsSearch", { kind: "Sprite", limit: 5_000 });
    expect(onChange).toHaveBeenCalledWith({
      kind: "AssetReference",
      assetName: "remolding/support",
    });
  });

  it("picks binding types and project effects, updating suggestions when the type changes", async () => {
    templateTypesCache.types = null;
    invalidateProjectClonesCache();
    const instances = [
      { className: "SkillTemplate", name: "effect.vanilla" },
      { className: "PerkTemplate", name: "passive.vanilla" },
    ];
    vi.mocked(rpcCall).mockImplementation((method, params) => {
      if (method === "templatesProjectClones") {
        return Promise.resolve({
          clones: [
            { templateType: "SkillTemplate", id: "effect.remolding", file: "remolding.kdl" },
            { templateType: "PerkTemplate", id: "passive.remolding", file: "remolding.kdl" },
          ],
        });
      }
      const type = (params as { className?: string } | undefined)?.className;
      return Promise.resolve({
        suggestions: type
          ? instances.filter((i) => i.className === type).map((i) => i.name)
          : instances.map((i) => i.className),
      });
    });
    const onChange = vi.fn();
    render(
      createElement(Controlled, {
        initial: { kind: "NumericPlaceholder", bindingFormat: "number" },
        onChange,
      }),
    );
    const typeInput = screen.getByLabelText("Source template type");
    fireEvent.focus(typeInput);
    fireEvent.click(await screen.findByRole("button", { name: "SkillTemplate" }));
    const idInput = screen.getByLabelText("Source template ID");
    fireEvent.focus(idInput);
    expect(await screen.findByRole("button", { name: "effect.vanilla" })).toBeDefined();
    fireEvent.click(await screen.findByRole("button", { name: /effect\.remolding\s*clone/ }));
    expect(onChange.mock.lastCall?.[0]).toMatchObject({
      kind: "NumericPlaceholder",
      referenceType: "SkillTemplate",
      referenceId: "effect.remolding",
    });

    fireEvent.change(typeInput, { target: { value: "Perk" } });
    fireEvent.click(await screen.findByRole("button", { name: "PerkTemplate" }));
    fireEvent.change(idInput, { target: { value: "" } });
    expect(await screen.findByRole("button", { name: /passive\.remolding\s*clone/ })).toBeDefined();
    expect(screen.queryByRole("button", { name: "effect.vanilla" })).toBeNull();
    fireEvent.change(idInput, { target: { value: "passive.new_content" } });
    expect(onChange.mock.lastCall?.[0]).toMatchObject({
      referenceType: "PerkTemplate",
      referenceId: "passive.new_content",
    });
  });

  it("Boolean renders checkbox, toggles on click", () => {
    const onChange = vi.fn();
    render(createElement(Controlled, { initial: { kind: "Boolean", boolean: false }, onChange }));
    const checkbox = screen.getByRole<HTMLInputElement>("checkbox");
    expect(checkbox.checked).toBe(false);
    fireEvent.click(checkbox);
    expect(onChange).toHaveBeenCalledWith({ kind: "Boolean", boolean: true });
  });

  it("Int32 renders number input, commits on blur", () => {
    const onChange = vi.fn();
    render(createElement(Controlled, { initial: { kind: "Int32", int32: 42 }, onChange }));
    const input = screen.getByRole<HTMLInputElement>("spinbutton");
    expect(input.value).toBe("42");
    fireEvent.change(input, { target: { value: "99" } });
    fireEvent.blur(input);
    expect(onChange).toHaveBeenCalledWith({ kind: "Int32", int32: 99 });
  });

  it("String renders text input", () => {
    const onChange = vi.fn();
    render(createElement(Controlled, { initial: { kind: "String", string: "hello" }, onChange }));
    const input = screen.getByRole<HTMLInputElement>("textbox");
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
    const input = screen.getByPlaceholderText<HTMLInputElement>("ItemSlot value");
    expect(input.value).toBe("Sword");
  });

  it("Null renders a visible chip with the literal 'null' text", () => {
    render(
      createElement(ValueEditor, {
        value: { kind: "Null" },
        onChange: vi.fn(),
      }),
    );
    expect(screen.getByText("null")).toBeTruthy();
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
    const input = screen.getByPlaceholderText<HTMLInputElement>("UnitTemplate id");
    expect(input.value).toBe("archer_01");
  });
});
