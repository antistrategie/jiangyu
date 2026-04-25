import { describe, expect, it } from "vitest";
import {
  beginTemplateDrag,
  encodeCrossInstancePayload,
  endTemplateDrag,
  getActiveTemplateDrag,
  parseCrossInstancePayload,
} from "@lib/drag/crossInstance.ts";

describe("encode/parseCrossInstancePayload", () => {
  it("round-trips an instance drag payload", () => {
    const raw = encodeCrossInstancePayload({
      name: "player_squad.darby",
      className: "EntityTemplate",
    });
    expect(parseCrossInstancePayload(raw)).toEqual({
      m: "jiangyu-instance-drag/1",
      name: "player_squad.darby",
      className: "EntityTemplate",
    });
  });

  it("returns null for empty input", () => {
    expect(parseCrossInstancePayload("")).toBeNull();
  });

  it("returns null for non-JSON input", () => {
    expect(parseCrossInstancePayload("not json")).toBeNull();
  });

  it("returns null when the marker is missing", () => {
    expect(parseCrossInstancePayload(JSON.stringify({ name: "x", className: "Y" }))).toBeNull();
  });

  it("returns null when the marker is wrong (e.g. crossMember payload)", () => {
    const raw = JSON.stringify({
      m: "jiangyu-member-drag/1",
      name: "x",
      className: "Y",
    });
    expect(parseCrossInstancePayload(raw)).toBeNull();
  });

  it("rejects payloads with non-string fields", () => {
    const raw = JSON.stringify({
      m: "jiangyu-instance-drag/1",
      name: 42,
      className: "Y",
    });
    expect(parseCrossInstancePayload(raw)).toBeNull();
  });

  it("returns null for JSON null without throwing", () => {
    expect(parseCrossInstancePayload("null")).toBeNull();
  });

  it("returns null for JSON primitives without throwing", () => {
    expect(parseCrossInstancePayload("123")).toBeNull();
    expect(parseCrossInstancePayload('"hello"')).toBeNull();
  });
});

describe("same-window drag context", () => {
  it("tracks active template drag until endTemplateDrag is called", () => {
    endTemplateDrag();
    expect(getActiveTemplateDrag()).toBeNull();

    beginTemplateDrag({
      kind: "instance",
      name: "player_squad.darby",
      className: "EntityTemplate",
    });
    expect(getActiveTemplateDrag()).toEqual({
      kind: "instance",
      name: "player_squad.darby",
      className: "EntityTemplate",
    });

    beginTemplateDrag({
      kind: "member",
      templateType: "EntityTemplate",
      fieldPath: "Properties.Accuracy",
    });
    expect(getActiveTemplateDrag()).toEqual({
      kind: "member",
      templateType: "EntityTemplate",
      fieldPath: "Properties.Accuracy",
    });

    endTemplateDrag();
    expect(getActiveTemplateDrag()).toBeNull();
  });
});
