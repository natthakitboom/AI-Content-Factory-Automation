import { describe, expect, it } from "vitest";
import { canApprove, canReject, emptyChecklist, isFullyChecked } from "./review";
import type { ChecklistItem } from "./types";

const items: ChecklistItem[] = [
  { key: "a", label: "A" },
  { key: "b", label: "B" },
];

describe("review decisions", () => {
  it("starts with nothing ticked", () => {
    expect(emptyChecklist(items)).toEqual({ a: false, b: false });
  });

  it("is fully checked only when every item is ticked", () => {
    expect(isFullyChecked(items, { a: true, b: false })).toBe(false);
    expect(isFullyChecked(items, { a: true })).toBe(false);
    expect(isFullyChecked(items, { a: true, b: true })).toBe(true);
    expect(isFullyChecked([], {})).toBe(false);
  });

  it("only allows approval for a QA-passed version awaiting review with a full checklist", () => {
    const base = { status: "awaiting_review", qaPassed: true, items, ticked: { a: true, b: true } };
    expect(canApprove(base)).toBe(true);
    expect(canApprove({ ...base, status: "qa_failed" })).toBe(false);
    expect(canApprove({ ...base, status: "approved" })).toBe(false);
    expect(canApprove({ ...base, qaPassed: false })).toBe(false);
    expect(canApprove({ ...base, ticked: { a: true, b: false } })).toBe(false);
  });

  it("requires a comment to reject", () => {
    expect(canReject({ status: "awaiting_review", comment: "  " })).toBe(false);
    expect(canReject({ status: "awaiting_review", comment: "logo is wrong" })).toBe(true);
    expect(canReject({ status: "approved", comment: "late" })).toBe(false);
  });
});
