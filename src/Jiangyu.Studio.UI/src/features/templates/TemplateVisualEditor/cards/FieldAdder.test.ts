// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";

afterEach(cleanup);
import type { TemplateMember } from "@shared/rpc";

vi.mock("../TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

vi.mock("@shared/rpc", () => ({
  rpcCall: vi.fn(),
}));

vi.mock("lucide-react", () => ({
  Plus: (props: Record<string, unknown>) => createElement("svg", props),
}));

vi.mock("@features/templates/crossInstance", () => ({
  INSTANCE_DRAG_TAG: "application/jiangyu-template-instance",
  MEMBER_DRAG_TAG: "application/jiangyu-template-member",
  getActiveTemplateDrag: vi.fn(() => null),
}));

import { FieldAdder, type FieldAdderProps } from "./FieldAdder";

function makeMember(overrides: Partial<TemplateMember> & { name: string }): TemplateMember {
  return {
    typeName: "Int32",
    isWritable: true,
    isInherited: false,
    ...overrides,
  };
}

const members: TemplateMember[] = [
  makeMember({ name: "health", typeName: "Int32", patchScalarKind: "Int32" }),
  makeMember({ name: "speed", typeName: "Single", patchScalarKind: "Single" }),
  makeMember({
    name: "tags",
    typeName: "List<String>",
    isCollection: true,
    elementTypeName: "String",
  }),
  makeMember({ name: "hidden", typeName: "Int32", isHiddenInInspector: true }),
];

function renderAdder(overrides: Partial<FieldAdderProps> = {}) {
  const onAdd = vi.fn();
  const result = render(
    createElement(FieldAdder, {
      members,
      membersLoaded: true,
      existingFields: [],
      targetTemplateType: "UnitTemplate",
      onAdd,
      ...overrides,
    }),
  );
  return { onAdd, ...result };
}

beforeEach(() => {
  vi.clearAllMocks();
});

describe("FieldAdder", () => {
  it.each([
    ["SkillsGranted", "SkillTemplate", "TemplateReference", { referenceId: "" }],
    ["Tags", "TagTemplate", "TemplateReference", { referenceId: "" }],
    ["Amounts", "Int32", "Int32", { int32: 0 }],
    ["m_Placeholders", "String", "String", { string: "" }],
    ["Modes", "SkillType", "Enum", { enumType: "SkillType", enumValue: "" }],
    ["Icons", "Sprite", "AssetReference", { assetName: "" }],
  ] as const)("edits a value at an index for %s", (name, elementTypeName, kind, payload) => {
    const onStartDescent = vi.fn();
    const { onAdd } = renderAdder({
      members: [
        makeMember({
          name,
          typeName: `List<${elementTypeName}>`,
          isCollection: true,
          elementTypeName,
          patchScalarKind: kind,
        }),
      ],
      onStartDescent,
    });
    fireEvent.focus(screen.getByPlaceholderText("Add field…"));
    fireEvent.click(screen.getByRole("button", { name: `↳ Edit slot of ${name}…` }));
    expect(onStartDescent).not.toHaveBeenCalled();
    expect(onAdd).toHaveBeenCalledWith(
      expect.objectContaining({
        op: "Set",
        fieldPath: name,
        index: 0,
        value: { kind, ...payload },
      }),
    );
  });

  it("allows indexed value edits inside an object editor", () => {
    const { onAdd } = renderAdder({
      members: [
        makeMember({
          name: "m_Placeholders",
          typeName: "String[]",
          isCollection: true,
          elementTypeName: "String",
          patchScalarKind: "String",
        }),
      ],
    });
    fireEvent.focus(screen.getByPlaceholderText("Add field…"));
    fireEvent.click(screen.getByRole("button", { name: "↳ Edit slot of m_Placeholders…" }));
    expect(onAdd).toHaveBeenCalledWith(
      expect.objectContaining({
        op: "Set",
        fieldPath: "m_Placeholders",
        index: 0,
      }),
    );
  });

  it.each([
    ["EventHandlers", "SkillEventHandlerTemplate", ["Attack", "Damage"]],
    ["Properties", "PropertyChange", null],
  ] as const)("retains field editing for %s objects", (name, elementTypeName, elementSubtypes) => {
    const onStartDescent = vi.fn();
    const { onAdd } = renderAdder({
      members: [
        makeMember({
          name,
          typeName: `List<${elementTypeName}>`,
          isCollection: true,
          elementTypeName,
          elementSubtypes: elementSubtypes ? [...elementSubtypes] : null,
        }),
      ],
      onStartDescent,
    });
    fireEvent.focus(screen.getByPlaceholderText("Add field…"));
    fireEvent.click(screen.getByRole("button", { name: `↳ Edit slot of ${name}…` }));
    expect(onStartDescent).toHaveBeenCalledWith(name, elementTypeName, elementSubtypes);
    expect(onAdd).not.toHaveBeenCalled();
  });

  it("keeps object slot edits hidden when there is no descent editor", () => {
    renderAdder({
      members: [
        makeMember({
          name: "Properties",
          typeName: "List<PropertyChange>",
          isCollection: true,
          elementTypeName: "PropertyChange",
        }),
      ],
    });
    fireEvent.focus(screen.getByPlaceholderText("Add field…"));
    expect(screen.getByText("Properties")).toBeDefined();
    expect(screen.queryByRole("button", { name: /Edit slot/ })).toBeNull();
  });

  it("reserves sets, matrix grids and named arrays for their existing editors", () => {
    renderAdder({
      members: [
        makeMember({
          name: "TagSet",
          typeName: "HashSet<String>",
          isCollection: true,
          elementTypeName: "String",
          patchScalarKind: "String",
          isOdinHashSet: true,
        }),
        makeMember({
          name: "InitialAttributes",
          typeName: "Byte[]",
          isCollection: true,
          elementTypeName: "Byte",
          patchScalarKind: "Byte",
          namedArrayEnumTypeName: "AttributeType",
        }),
        makeMember({
          name: "AOETiles",
          typeName: "Byte[,]",
          isCollection: true,
          elementTypeName: "Byte",
          isOdinMultiDimArray: true,
        }),
      ],
      onStartDescent: vi.fn(),
      onAddMatrix: vi.fn(),
    });
    fireEvent.focus(screen.getByPlaceholderText("Add field…"));
    expect(screen.getByText("TagSet")).toBeDefined();
    expect(screen.getByText("AOETiles")).toBeDefined();
    expect(screen.getByText("InitialAttributes")).toBeDefined();
    expect(screen.queryByRole("button", { name: /Edit slot/ })).toBeNull();
  });

  it("renders search input with placeholder", () => {
    renderAdder();
    expect(screen.getByPlaceholderText("Add field…")).toBeDefined();
  });

  it("filters members by query", () => {
    renderAdder();
    const input = screen.getByPlaceholderText("Add field…");
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "hea" } });
    expect(screen.getByText("health")).toBeDefined();
    expect(screen.queryByText("speed")).toBeNull();
  });

  it("calls onAdd when member selected", () => {
    const { onAdd } = renderAdder();
    const input = screen.getByPlaceholderText("Add field…");
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "" } });
    // Click the "health" button.
    fireEvent.click(screen.getByText("health"));
    expect(onAdd).toHaveBeenCalledTimes(1);
    expect(onAdd.mock.calls[0]![0]).toMatchObject({ fieldPath: "health" });
  });

  it('shows "Already added" section for existing scalar fields', () => {
    renderAdder({ existingFields: ["health"] });
    const input = screen.getByPlaceholderText("Add field…");
    fireEvent.focus(input);
    expect(screen.getByText("Already added")).toBeDefined();
  });

  it("multi-directive fields (collections) stay in available list even when existing", () => {
    renderAdder({ existingFields: ["tags"] });
    const input = screen.getByPlaceholderText("Add field…");
    fireEvent.focus(input);
    // "tags" is a collection, so it should NOT appear under "Already added".
    expect(screen.queryByText("Already added")).toBeNull();
    // It should still be clickable in the main list.
    expect(screen.getByText("tags")).toBeDefined();
  });

  it("keeps an Odin-routed polymorphic list whose elements are constructible", () => {
    renderAdder({
      members: [
        makeMember({
          name: "Effects",
          typeName: "BaseGameEffect[]",
          isCollection: true,
          isLikelyOdinOnly: true,
          elementTypeName: "BaseGameEffect",
          elementSubtypes: ["ChangeOffmapAbilityDelayEffect"],
        }),
        makeMember({
          name: "Opaque",
          typeName: "BaseGameEffect[]",
          isCollection: true,
          isLikelyOdinOnly: true,
          elementTypeName: "BaseGameEffect",
        }),
      ],
    });
    const input = screen.getByPlaceholderText("Add field…");
    fireEvent.focus(input);
    expect(screen.getByText("Effects")).toBeDefined();
    expect(screen.queryByText("Opaque")).toBeNull();
  });
});
