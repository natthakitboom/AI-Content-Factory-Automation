import type { ChecklistItem } from "./types";

export type Ticks = Record<string, boolean>;

export function emptyChecklist(items: ChecklistItem[]): Ticks {
  return Object.fromEntries(items.map((item) => [item.key, false]));
}

export function isFullyChecked(items: ChecklistItem[], ticked: Ticks): boolean {
  return items.length > 0 && items.every((item) => ticked[item.key] === true);
}

/** Mirrors the server rule; the server still decides. There is no approve-all shortcut. */
export function canApprove(input: {
  status: string;
  qaPassed: boolean;
  items: ChecklistItem[];
  ticked: Ticks;
}): boolean {
  return input.status === "awaiting_review" && input.qaPassed && isFullyChecked(input.items, input.ticked);
}

export function canReject(input: { status: string; comment: string }): boolean {
  return input.status === "awaiting_review" && input.comment.trim().length > 0;
}
