import { describe, it, expect } from "vitest";
import { skippedValuesNote } from "./helpers";

describe("skippedValuesNote", () => {
  it("is null when nothing was skipped", () => {
    expect(skippedValuesNote(0)).toBeNull();
    expect(skippedValuesNote(null)).toBeNull();
    expect(skippedValuesNote(undefined)).toBeNull();
  });

  it("names the count and what it means", () => {
    expect(skippedValuesNote(1)).toBe(
      "1 template has no values in the index. Its fields show nothing until the cause is fixed and the index rebuilt.",
    );
    expect(skippedValuesNote(40)).toMatch(/^40 templates have no values in the index\. Their fields/);
  });

  it("quotes the first skipped instance when the status carries one", () => {
    const note = skippedValuesNote(2, ["resources.assets:12: IOException: bad image", "other"]);
    expect(note).toMatch(/ First: resources\.assets:12: IOException: bad image\.$/);
    expect(skippedValuesNote(2, [])).not.toMatch(/First:/);
  });
});
