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
});
