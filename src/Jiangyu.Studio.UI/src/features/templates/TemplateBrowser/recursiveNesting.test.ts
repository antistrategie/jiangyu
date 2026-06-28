// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { createElement } from "react";
import { render, screen, fireEvent, cleanup } from "@testing-library/react";
import type { InspectedFieldNode, TemplateInstanceEntry } from "@shared/rpc";

afterEach(cleanup);

vi.mock("./TemplateBrowser.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

vi.mock("lucide-react", () => ({
  GripVertical: () => null,
}));

import { NestedValueRow } from "./DetailPanel";
import { buildReferenceTargetIndex, instanceKey } from "./helpers";

// Mirrors a mission's m_ObjectiveGroups[].Objectives[].Target shape: an array
// of polymorphic objective configs, each carrying an enemy EntityTemplate
// reference. Before the recursive renderer this nesting dead-ended at the
// inner Objectives array and the enemy ref was never reachable.
const captain: TemplateInstanceEntry = {
  name: "enemy.pirate_captain",
  className: "EntityTemplate",
  identity: { collection: "resources.assets", pathId: 42 },
};

const objectivesArray: InspectedFieldNode = {
  kind: "array",
  name: "Objectives",
  fieldTypeName: "ObjectiveConfig[]",
  count: 1,
  elements: [
    {
      kind: "object",
      fieldTypeName: "KillUnitConfig",
      fields: [
        {
          kind: "reference",
          name: "Target",
          fieldTypeName: "EntityTemplate",
          reference: { pathId: 42, name: "enemy.pirate_captain", className: "EntityTemplate" },
        },
      ],
    },
  ],
};

function renderRow(onNavigate = vi.fn()) {
  const instanceLookup = new Map<string, TemplateInstanceEntry>([[instanceKey(captain), captain]]);
  const referenceTargetIndex = buildReferenceTargetIndex([captain]);
  render(
    createElement(NestedValueRow, {
      value: objectivesArray,
      instanceLookup,
      referenceTargetIndex,
      onNavigate,
    }),
  );
  return onNavigate;
}

describe("NestedValueRow deep recursion", () => {
  it("expands a nested array node instead of dead-ending at a count summary", () => {
    renderRow();

    // Collapsed: the array shows a count summary and an expand affordance.
    expect(screen.getByText("Objectives")).toBeDefined();
    expect(screen.getByText("[1 items]")).toBeDefined();

    // Expanding the array reveals its polymorphic element row.
    fireEvent.click(screen.getByRole("button", { name: "Expand" }));
    expect(screen.getByText("[0]")).toBeDefined();
    expect(screen.getByText("KillUnitConfig")).toBeDefined();
  });

  it("renders a navigable link for an enemy reference buried under two array/object levels", () => {
    const onNavigate = renderRow();

    fireEvent.click(screen.getByRole("button", { name: "Expand" })); // open Objectives
    fireEvent.click(screen.getByRole("button", { name: "Expand" })); // open KillUnitConfig element

    const link = screen.getByRole("button", { name: "EntityTemplate:enemy.pirate_captain" });
    fireEvent.click(link);
    expect(onNavigate).toHaveBeenCalledWith("resources.assets:42");
  });

  it("keeps a NamedArray element's member name instead of a positional index", () => {
    const namedArray: InspectedFieldNode = {
      kind: "array",
      name: "InitialAttributes",
      count: 1,
      // NamedArray slots carry the paired enum-member name on the element node.
      elements: [
        { kind: "object", name: "Vitality", fields: [{ kind: "int", name: "value", value: 70 }] },
      ],
    };
    render(
      createElement(NestedValueRow, {
        value: namedArray,
        instanceLookup: new Map(),
        referenceTargetIndex: buildReferenceTargetIndex([]),
        onNavigate: vi.fn(),
      }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Expand" }));
    expect(screen.getByText("Vitality")).toBeDefined();
    expect(screen.queryByText("[0]")).toBeNull();
  });

  it("renders a nested 2D matrix as a grid instead of a blank row", () => {
    const matrix: InspectedFieldNode = {
      kind: "matrix",
      name: "AOETiles",
      dimensions: [1, 2],
      elements: [
        { kind: "int", value: 11 },
        { kind: "int", value: 22 },
      ],
    };
    render(
      createElement(NestedValueRow, {
        value: matrix,
        instanceLookup: new Map(),
        referenceTargetIndex: buildReferenceTargetIndex([]),
        onNavigate: vi.fn(),
      }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Expand" }));
    expect(screen.getByText("11")).toBeDefined();
    expect(screen.getByText("22")).toBeDefined();
  });

  it("surfaces a truncation notice for a truncated nested array", () => {
    const truncated: InspectedFieldNode = {
      kind: "array",
      name: "Spawns",
      count: 200,
      truncated: true,
      elements: [{ kind: "object", fields: [{ kind: "int", name: "x", value: 1 }] }],
    };
    render(
      createElement(NestedValueRow, {
        value: truncated,
        instanceLookup: new Map(),
        referenceTargetIndex: buildReferenceTargetIndex([]),
        onNavigate: vi.fn(),
      }),
    );

    fireEvent.click(screen.getByRole("button", { name: "Expand" }));
    expect(screen.getByText(/truncated \(total 200\)/)).toBeDefined();
  });
});
