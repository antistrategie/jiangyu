// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
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

import { MatrixFieldEditor } from "./MatrixEditor";
import { isFlagsEnum, stripArraySuffix } from "./matrixHelpers";
import { EditorDispatchContext, NodeIndexContext } from "../store";

describe("isFlagsEnum", () => {
  it("accepts standard [Flags] enums (zero plus powers of two)", () => {
    expect(
      isFlagsEnum([
        { name: "None", value: 0 },
        { name: "A", value: 1 },
        { name: "B", value: 2 },
        { name: "C", value: 4 },
      ]),
    ).toBe(true);
  });

  it("accepts a [Flags] enum with combined alias members removed", () => {
    expect(
      isFlagsEnum([
        { name: "None", value: 0 },
        { name: "Occupied", value: 1 },
        { name: "Road", value: 2 },
        { name: "CoverN", value: 4 },
        { name: "CoverE", value: 8 },
        { name: "CoverS", value: 16 },
        { name: "CoverW", value: 32 },
        { name: "GroundTexture", value: 64 },
        { name: "RoadToMapBorder", value: 128 },
      ]),
    ).toBe(true);
  });

  it("rejects sequential-value enums", () => {
    expect(
      isFlagsEnum([
        { name: "Basic", value: 0 },
        { name: "Improved", value: 1 },
        { name: "Advanced", value: 2 },
        { name: "Master", value: 3 },
      ]),
    ).toBe(false);
  });

  it("rejects single-bit enums (need >= 2 powers of two)", () => {
    expect(
      isFlagsEnum([
        { name: "None", value: 0 },
        { name: "Only", value: 1 },
      ]),
    ).toBe(false);
  });

  it("rejects enums with negative values", () => {
    expect(
      isFlagsEnum([
        { name: "Sentinel", value: -1 },
        { name: "A", value: 1 },
        { name: "B", value: 2 },
      ]),
    ).toBe(false);
  });

  it("rejects empty member lists", () => {
    expect(isFlagsEnum([])).toBe(false);
  });
});

describe("stripArraySuffix", () => {
  it("strips bare 1D array brackets", () => {
    expect(stripArraySuffix("Foo[]")).toBe("Foo");
  });

  it("strips 2D array brackets", () => {
    expect(stripArraySuffix("Menace.Tactical.Mapgen.ChunkTileFlags[,]")).toBe(
      "Menace.Tactical.Mapgen.ChunkTileFlags",
    );
  });

  it("strips 3D array brackets", () => {
    expect(stripArraySuffix("Foo.Bar[,,]")).toBe("Foo.Bar");
  });

  it("leaves non-array type names untouched", () => {
    expect(stripArraySuffix("Boolean")).toBe("Boolean");
  });
});

function renderEditor(
  props: Omit<Parameters<typeof MatrixFieldEditor>[0], "onRemove"> & { onRemove?: () => void },
) {
  const dispatch = vi.fn();
  const onRemove = props.onRemove ?? vi.fn();
  const ui = createElement(
    EditorDispatchContext.Provider,
    { value: dispatch },
    createElement(
      NodeIndexContext.Provider,
      { value: 0 },
      createElement(MatrixFieldEditor, { ...props, onRemove }),
    ),
  );
  return { dispatch, onRemove, ...render(ui) };
}

const odinBoolMember: TemplateMember = {
  name: "AOETiles",
  typeName: "Il2CppObjectBase",
  isWritable: true,
  isInherited: false,
  isLikelyOdinOnly: true,
  isOdinMultiDimArray: true,
  multiDimRank: 2,
  multiDimDimensions: [3, 3],
  multiDimElementType: "Boolean",
  multiDimElementKind: "bool",
};

describe("MatrixFieldEditor (no-vanilla, catalog-driven)", () => {
  it("renders a grid sized from catalog dimensions", () => {
    renderEditor({
      fieldName: "AOETiles",
      matrix: null,
      member: odinBoolMember,
      directives: [],
    });
    // 3x3 = 9 cell buttons + 1 X delete button = 10 buttons total.
    expect(screen.getAllByRole("button")).toHaveLength(10);
  });

  it("dispatches an addDirective with cell=[r,c] when a cell is clicked", () => {
    const { dispatch } = renderEditor({
      fieldName: "AOETiles",
      matrix: null,
      member: odinBoolMember,
      directives: [],
    });
    const cells = screen
      .getAllByRole("button")
      .filter((b) => b.getAttribute("title")?.startsWith("["));
    fireEvent.click(cells[4]!); // (1, 1) in a 3x3 grid
    expect(dispatch).toHaveBeenCalledWith(
      expect.objectContaining({
        type: "addDirective",
        nodeIndex: 0,
        directive: expect.objectContaining({
          op: "Set",
          fieldPath: "AOETiles",
          indexPath: [1, 1],
          value: { kind: "Boolean", boolean: true },
        }),
      }),
    );
  });

  it("absorbs an existing cell directive into the grid (no double-render)", () => {
    renderEditor({
      fieldName: "AOETiles",
      matrix: null,
      member: odinBoolMember,
      directives: [
        {
          op: "Set",
          fieldPath: "AOETiles",
          indexPath: [0, 0],
          value: { kind: "Boolean", boolean: true },
          _uiId: "u1",
        },
      ],
    });
    // Cell (0,0) shows the "true" glyph; "1 cell edited" header is present.
    expect(screen.getByText(/1 cell edited/)).toBeTruthy();
  });

  it("invokes onRemove when the X button is clicked", () => {
    const onRemove = vi.fn();
    renderEditor({
      fieldName: "AOETiles",
      matrix: null,
      member: odinBoolMember,
      directives: [],
      onRemove,
    });
    const xButton = screen.getByTitle("Remove matrix field");
    fireEvent.click(xButton);
    expect(onRemove).toHaveBeenCalledTimes(1);
  });

  it("renders nothing when there is no shape and no directives", () => {
    const { container } = renderEditor({
      fieldName: "AOETiles",
      matrix: null,
      member: null,
      directives: [],
    });
    expect(container.firstChild).toBeNull();
  });
});
