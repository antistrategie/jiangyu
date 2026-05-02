import { describe, it, expect } from "vitest";
import { allowsMultipleDirectives } from "./helpers";

describe("allowsMultipleDirectives", () => {
  it("is true for collections", () => {
    expect(allowsMultipleDirectives({ isCollection: true })).toBe(true);
  });

  it("is false for non-collection scalars", () => {
    expect(allowsMultipleDirectives({})).toBe(false);
  });

  it("is false when isCollection is null", () => {
    expect(allowsMultipleDirectives({ isCollection: null })).toBe(false);
  });
});
