// @vitest-environment jsdom
import { afterEach, expect, it, vi } from "vitest";
import { createElement, useReducer } from "react";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { TemplateMember } from "@shared/rpc";
import type { StampedNode } from "../helpers";
import {
  EditorDispatchContext,
  NodeIndexContext,
  editorReducer,
  type EditorAction,
} from "../store";
import { DirectiveBody } from "./DirectiveBody";

afterEach(cleanup);

vi.mock("@tanstack/react-virtual", () => ({
  useVirtualizer: (opts: { count: number }) => ({
    getTotalSize: () => opts.count * 28,
    getVirtualItems: () =>
      Array.from({ length: opts.count }, (_, index) => ({
        index,
        start: index * 28,
        size: 28,
        key: index,
      })),
  }),
}));

const rpcCall = vi.fn((method: string) =>
  Promise.resolve(
    method === "templatesProjectClones"
      ? {
          clones: [
            { templateType: "SkillTemplate", id: "active.remolding", file: "remolding.kdl" },
          ],
        }
      : { suggestions: ["active.first_aid"] },
  ),
);
vi.mock("@shared/rpc", () => ({ rpcCall: (...args: [string]) => rpcCall(...args) }));

const skills: TemplateMember = {
  name: "SkillsGranted",
  typeName: "List<SkillTemplate>",
  isWritable: true,
  isInherited: true,
  isCollection: true,
  patchScalarKind: "TemplateReference",
  elementTypeName: "SkillTemplate",
  referenceTypeName: "SkillTemplate",
};

it("chooses a skill for an existing slot and keeps the replacement when the index changes", async () => {
  const onAction = vi.fn();
  function SlotEditor() {
    const [nodes, dispatch] = useReducer(editorReducer, [
      {
        _uiId: "accessory",
        kind: "Patch",
        templateType: "AccessoryTemplate",
        templateId: "accessory.binoculars",
        directives: [],
      },
    ] as StampedNode[]);
    return createElement(
      EditorDispatchContext.Provider,
      {
        value: (action: EditorAction) => {
          onAction(action);
          dispatch(action);
        },
      },
      createElement(
        NodeIndexContext.Provider,
        { value: 0 },
        createElement(DirectiveBody, {
          node: nodes[0]!,
          members: [skills],
          membersLoaded: true,
          memberMap: new Map([[skills.name, skills]]),
          vanillaFields: new Map(),
          matrixFieldNames: new Set<string>(),
          onAddMatrix: vi.fn(),
          handleNodeDrop: vi.fn(),
        }),
      ),
    );
  }
  render(createElement(SlotEditor));
  fireEvent.focus(screen.getByPlaceholderText("Add field…"));
  fireEvent.click(screen.getByRole("button", { name: "↳ Edit slot of SkillsGranted…" }));

  const picker = screen.getByPlaceholderText("SkillTemplate id");
  expect(screen.getAllByPlaceholderText("Add field…")).toHaveLength(1);
  fireEvent.focus(picker);
  fireEvent.click(await screen.findByRole("button", { name: "active.first_aid" }));
  expect(onAction.mock.lastCall?.[0].directive).toMatchObject({
    op: "Set",
    fieldPath: "SkillsGranted",
    index: 0,
    value: { kind: "TemplateReference", referenceId: "active.first_aid" },
  });

  const index = screen.getByRole("spinbutton");
  fireEvent.change(index, { target: { value: "1" } });
  fireEvent.blur(index);
  expect(onAction.mock.lastCall?.[0].directive).toMatchObject({
    index: 1,
    value: { kind: "TemplateReference", referenceId: "active.first_aid" },
  });
  fireEvent.change(picker, { target: { value: "remolding" } });
  fireEvent.click(await screen.findByRole("button", { name: /active\.remolding\s*clone/ }));
  expect(onAction.mock.lastCall?.[0].directive).toEqual({
    _uiId: expect.any(String),
    op: "Set",
    fieldPath: "SkillsGranted",
    index: 1,
    value: { kind: "TemplateReference", referenceId: "active.remolding" },
  });
  expect(rpcCall.mock.calls.some(([method]) => method === "templatesQuery")).toBe(false);
});
