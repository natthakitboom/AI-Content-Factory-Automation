const TONES: Record<string, { tone: "ok" | "warn" | "bad" | "neutral"; glyph: string; label: string }> = {
  awaiting_review: { tone: "warn", glyph: "◔", label: "Awaiting review" },
  approved: { tone: "ok", glyph: "✓", label: "Approved" },
  rejected: { tone: "bad", glyph: "✕", label: "Rejected" },
  qa_failed: { tone: "bad", glyph: "✕", label: "QA failed" },
  blocked: { tone: "bad", glyph: "■", label: "Blocked" },
  building: { tone: "neutral", glyph: "…", label: "Building" },
  succeeded: { tone: "ok", glyph: "✓", label: "Succeeded" },
  failed: { tone: "bad", glyph: "✕", label: "Failed" },
  running: { tone: "warn", glyph: "▶", label: "Running" },
  queued: { tone: "neutral", glyph: "…", label: "Queued" },
  retry_wait: { tone: "warn", glyph: "⟳", label: "Waiting to retry" },
  paused: { tone: "warn", glyph: "❚❚", label: "Paused" },
  cancelled: { tone: "neutral", glyph: "–", label: "Cancelled" },
};

/** Status is always conveyed by a glyph and a word, never by colour alone. */
export function StatusBadge({ status }: { status: string }) {
  const entry = TONES[status] ?? { tone: "neutral" as const, glyph: "•", label: status.replaceAll("_", " ") };
  return (
    <span className={`badge ${entry.tone}`} data-status={status}>
      <span aria-hidden="true">{entry.glyph}</span>
      {entry.label}
    </span>
  );
}
