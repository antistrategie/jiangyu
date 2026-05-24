// @vitest-environment jsdom
//
// Regression suite for the visual editor's per-context member-resolution.
// Each directive context (top-level, descent group, composite body, …) must
// look up its destination field on the correct member list: the outer template
// at the top level, the descent target inside a descent group, the composite
// type inside a CompositeEditor. Getting the wrong list silently falls back
// to generic "type unknown" UI (the type-selector combobox appears, enum
// dropdowns lose their member list, etc.). Add a fixture here whenever a new
// regression of that class surfaces.

import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup, waitFor } from "@testing-library/react";

afterEach(cleanup);

vi.mock("../TemplateVisualEditor.module.css", () => ({
  default: new Proxy({}, { get: (_, key) => key }),
}));

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

const rpcCallMock = vi.fn();
vi.mock("@shared/rpc", () => ({
  rpcCall: (...args: unknown[]) => rpcCallMock(...args),
}));

vi.mock("../shared/rpcHelpers", async () => {
  const actual = await vi.importActual<object>("../shared/rpcHelpers");
  return {
    ...actual,
    templatesPrototypeSupportedTypes: vi.fn(() => Promise.resolve(new Set<string>())),
    templatesPrototypeCandidates: vi.fn(() => Promise.resolve([])),
    getCachedTemplateTypes: vi.fn(() => Promise.resolve([])),
    getCachedProjectClones: vi.fn(() => Promise.resolve([])),
    templatesSearch: vi.fn(() => Promise.resolve({ instances: [] })),
  };
});

import { DirectiveBody } from "./DirectiveBody";
import {
  CompositeCollapseContext,
  EditorDispatchContext,
  NodeIndexContext,
  type CompositeCollapseControl,
} from "../store";
import type { StampedDirective, StampedNode } from "../helpers";
import type { EnumMemberEntry, TemplateMember } from "@shared/rpc";

// Force every composite open so inner-row rendering is reachable from
// tests without simulating a click on the collapse chevron.
const ALWAYS_EXPANDED: CompositeCollapseControl = {
  resolveState: () => false,
  toggle: () => {},
};

const PERK_TREE_TEMPLATE: TemplateMember[] = [
  {
    name: "Perks",
    typeName: "Perk[]",
    isWritable: true,
    isInherited: false,
    isCollection: true,
    elementTypeName: "Perk",
  },
  {
    name: "Skill",
    typeName: "PerkTemplate",
    isWritable: true,
    isInherited: false,
    isTemplateReference: true,
    patchScalarKind: "TemplateReference",
    referenceTypeName: "PerkTemplate",
  },
  {
    name: "DamageFilterCondition",
    typeName: "ITacticalCondition",
    isWritable: true,
    isInherited: false,
    isTemplateReference: true,
    patchScalarKind: "TemplateReference",
    referenceTypeName: "ITacticalCondition",
    isReferenceTypePolymorphic: true,
  },
  {
    name: "MoraleState",
    typeName: "MoraleState",
    isWritable: true,
    isInherited: false,
    isScalar: true,
    enumTypeName: "MoraleState",
    patchScalarKind: "Enum",
    enumMembers: [
      { name: "Calm", value: 0 },
      { name: "Fleeing", value: 1 },
    ] satisfies EnumMemberEntry[],
  },
  {
    name: "Stats",
    typeName: "PerkStats",
    isWritable: true,
    isInherited: false,
  },
];

const PERK_MEMBERS: TemplateMember[] = [
  {
    name: "Skill",
    typeName: "PerkTemplate",
    isWritable: true,
    isInherited: false,
    isTemplateReference: true,
    patchScalarKind: "TemplateReference",
    referenceTypeName: "PerkTemplate",
  },
  {
    name: "Tier",
    typeName: "Int32",
    isWritable: true,
    isInherited: false,
    isScalar: true,
    patchScalarKind: "Int32",
  },
];

const PERK_STATS_MEMBERS: TemplateMember[] = [
  {
    name: "Bonus",
    typeName: "PerkTemplate",
    isWritable: true,
    isInherited: false,
    isTemplateReference: true,
    patchScalarKind: "TemplateReference",
    referenceTypeName: "PerkTemplate",
  },
];

const FIXTURE_MEMBERS: Record<string, TemplateMember[]> = {
  PerkTreeTemplate: PERK_TREE_TEMPLATE,
  Perk: PERK_MEMBERS,
  PerkStats: PERK_STATS_MEMBERS,
};

beforeEach(() => {
  rpcCallMock.mockReset();
  rpcCallMock.mockImplementation((method: string, params: Record<string, unknown>) => {
    if (method === "templatesQuery") {
      const t = params.typeName as string;
      return Promise.resolve({
        kind: "type",
        members: FIXTURE_MEMBERS[t] ?? [],
      });
    }
    return Promise.resolve({});
  });
});

function memberMapFor(typeName: string): Map<string, TemplateMember> {
  return new Map((FIXTURE_MEMBERS[typeName] ?? []).map((m) => [m.name, m]));
}

function makeNode(directives: StampedDirective[]): StampedNode {
  return {
    _uiId: "n1",
    kind: "Patch",
    templateType: "PerkTreeTemplate",
    templateId: "perk_tree.sy",
    directives,
  };
}

function renderBody(directives: StampedDirective[]) {
  const dispatch = vi.fn();
  const ui = createElement(
    EditorDispatchContext.Provider,
    { value: dispatch },
    createElement(
      NodeIndexContext.Provider,
      { value: 0 },
      createElement(
        CompositeCollapseContext.Provider,
        { value: ALWAYS_EXPANDED },
        createElement(DirectiveBody, {
          node: makeNode(directives),
          members: FIXTURE_MEMBERS.PerkTreeTemplate ?? [],
          membersLoaded: true,
          memberMap: memberMapFor("PerkTreeTemplate"),
          vanillaFields: new Map(),
          matrixFieldNames: new Set<string>(),
          onAddMatrix: vi.fn(),
          handleNodeDrop: vi.fn(),
        }),
      ),
    ),
  );
  return { dispatch, ...render(ui) };
}

function directive(d: Partial<StampedDirective>): StampedDirective {
  return {
    _uiId: d._uiId ?? "d1",
    op: d.op ?? "Set",
    fieldPath: d.fieldPath ?? "",
    ...d,
  };
}

describe("member resolution by context", () => {
  it("top-level monomorphic ref hides the type selector", () => {
    renderBody([
      directive({
        fieldPath: "Skill",
        value: { kind: "TemplateReference", referenceId: "perk.vanguard" },
      }),
    ]);

    // Monomorphic ref → no `ref="…"` chrome, only the ID combobox.
    expect(screen.queryByPlaceholderText("Type")).toBeNull();
    expect(screen.getByPlaceholderText<HTMLInputElement>("PerkTemplate id").value).toBe(
      "perk.vanguard",
    );
  });

  it("top-level polymorphic ref shows the type selector", () => {
    renderBody([
      directive({
        fieldPath: "DamageFilterCondition",
        value: {
          kind: "TemplateReference",
          referenceType: "ProneCondition",
          referenceId: "",
        },
      }),
    ]);

    // Polymorphic → both the type selector and the ID combobox render.
    expect(screen.getByPlaceholderText("Type")).toBeDefined();
  });

  it("descent-group inner monomorphic ref hides the type selector", async () => {
    // Regression for the Perk.Skill bug: the inner row must resolve its
    // member info against Perk's members (referenceTypeName=PerkTemplate),
    // not the outer PerkTreeTemplate's. Pre-fix, no ref type was found and
    // the type-selector combobox appeared.
    renderBody([
      directive({
        _uiId: "inner1",
        fieldPath: "Skill",
        descent: [{ field: "Perks", index: 13 }],
        value: { kind: "TemplateReference", referenceId: "perk.vanguard" },
      }),
    ]);

    await waitFor(() => {
      expect(screen.getByPlaceholderText<HTMLInputElement>("PerkTemplate id").value).toBe(
        "perk.vanguard",
      );
    });
    expect(screen.queryByPlaceholderText("Type")).toBeNull();
  });

  it("descent-group inner scalar resolves its member's scalar kind", async () => {
    renderBody([
      directive({
        _uiId: "inner2",
        fieldPath: "Tier",
        descent: [{ field: "Perks", index: 13 }],
        value: { kind: "Int32", int32: 2 },
      }),
    ]);

    // Number spinbutton (Int32). Descent groups also render an index
    // input for the slot, so filter by the value-input class.
    await waitFor(() => {
      const inputs = screen.getAllByRole<HTMLInputElement>("spinbutton");
      const valueInput = inputs.find((el) => el.className.includes("setValueInput"));
      expect(valueInput?.value).toBe("2");
    });
  });

  it("top-level enum surfaces the declared enum type to its editor", () => {
    renderBody([
      directive({
        fieldPath: "MoraleState",
        value: { kind: "Enum", enumType: "MoraleState", enumValue: "Fleeing" },
      }),
    ]);

    // Enum editor sees referenceTypeName via member.enumTypeName and
    // renders the typed placeholder. If the member weren't resolved the
    // placeholder would degrade to a generic "Value".
    expect(screen.getByPlaceholderText<HTMLInputElement>("MoraleState value").value).toBe(
      "Fleeing",
    );
  });

  it("composite body inner monomorphic ref hides the type selector", async () => {
    // CompositeEditor fetches its own member list for the inline type via
    // useTemplateMembers (PerkStats here). The inner rows use that local
    // memberMap, so the Bonus ref must resolve as monomorphic against
    // PerkStats, not the outer PerkTreeTemplate.
    renderBody([
      directive({
        fieldPath: "Stats",
        value: {
          kind: "Composite",
          compositeType: "PerkStats",
          compositeDirectives: [
            {
              _uiId: "innerComp",
              op: "Set",
              fieldPath: "Bonus",
              value: { kind: "TemplateReference", referenceId: "perk.boost" },
            },
          ],
        },
      }),
    ]);

    await waitFor(() => {
      expect(screen.getByPlaceholderText<HTMLInputElement>("PerkTemplate id").value).toBe(
        "perk.boost",
      );
    });
    expect(screen.queryByPlaceholderText("Type")).toBeNull();
  });
});
