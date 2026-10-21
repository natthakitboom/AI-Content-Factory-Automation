"use client";

import { useState } from "react";
import { ExportPanel } from "@/components/ExportPanel";
import { StatusBadge } from "@/components/StatusBadge";
import { api, ApiError } from "@/lib/api";
import { formatMoney, shortHash } from "@/lib/format";
import { canApprove, canReject, emptyChecklist, type Ticks } from "@/lib/review";
import { useLoad } from "@/lib/useLoad";
import type { VideoReviewDetail } from "@/lib/types";

export function VideoReview({ id }: { id: string }) {
  const video = useLoad(() => api.get<VideoReviewDetail>(`/api/v1/videos/${id}`), [id]);
  const [ticked, setTicked] = useState<Ticks | null>(null);
  const [comment, setComment] = useState("");
  const [message, setMessage] = useState<{ kind: "ok" | "bad"; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [captions, setCaptions] = useState<Record<string, string>>({});

  if (video.error) {
    return <p role="alert" className="alert bad">Could not load this video: {video.error.message}</p>;
  }
  const detail = video.data;
  if (!detail) {
    return <p>Loading…</p>;
  }

  const ticks = ticked ?? emptyChecklist(detail.checklist);
  const qaPassed = detail.qa?.passed === true;
  const reviewable = detail.status === "awaiting_review";

  async function decide(approved: boolean) {
    if (!detail?.manifestSha256) {
      return;
    }
    setBusy(true);
    setMessage(null);
    try {
      await api.post(`/api/v1/videos/${id}/reviews`, {
        manifestSha256: detail.manifestSha256,
        approved,
        checklist: ticks,
        comment: comment.trim() || null,
      });
      setMessage({ kind: "ok", text: approved ? "Approved this exact version." : "Rejected." });
      await video.reload();
    } catch (caught) {
      setMessage({ kind: "bad", text: caught instanceof ApiError ? `${caught.message} (${caught.code})` : "The decision failed." });
    } finally {
      setBusy(false);
    }
  }

  async function saveCaption(sceneId: string) {
    const caption = captions[sceneId];
    if (!caption?.trim()) {
      return;
    }
    setBusy(true);
    setMessage(null);
    try {
      await api.post(`/api/v1/videos/${id}/caption-edits`, { sceneId, caption });
      setMessage({
        kind: "ok",
        text: "Caption saved as a new version being built. Approval does not carry over: review the new version when it appears in the review queue.",
      });
    } catch (caught) {
      setMessage({ kind: "bad", text: caught instanceof ApiError ? `${caught.message} (${caught.code})` : "The edit failed." });
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <h1>{detail.title || "Video"} · v{detail.versionNo}</h1>
      <p>
        <StatusBadge status={detail.status} /> <span className="muted mono" title={detail.manifestSha256 ?? ""}>manifest {shortHash(detail.manifestSha256)}</span>
      </p>
      {message ? <p role="status" className={`alert ${message.kind}`} data-testid="review-message">{message.text}</p> : null}
      {detail.usesAiMotion && detail.aiMotionWarning ? (
        <p role="alert" className="alert" data-testid="ai-motion-warning">⚠ AI-generated motion. {detail.aiMotionWarning}</p>
      ) : null}

      <div className="two-col">
        <section className="card" aria-labelledby="preview-h">
          <h2 id="preview-h">Preview</h2>
          {detail.mediaUrl ? (
            // eslint-disable-next-line jsx-a11y/media-has-caption
            <video controls preload="metadata" src={detail.mediaUrl} data-testid="preview-video" />
          ) : (
            <p>No rendered file yet.</p>
          )}
          <h3 style={{ marginTop: "1rem" }}>Cost for this video</h3>
          <p data-testid="video-cost">
            Reserved {formatMoney(detail.cost.reservedUsd, detail.cost.currency)} · Spent {formatMoney(detail.cost.settledUsd, detail.cost.currency)}
          </p>
          <h3>Technical checks</h3>
          {detail.qa ? (
            <p data-testid="qa-result">
              {detail.qa.passed ? "✓ Passed technical checks" : "✕ Did not pass technical checks"}
              {detail.qa.errors?.length ? (
                <ul>{detail.qa.errors.map((error) => <li key={error.code + error.path}>{error.message}</li>)}</ul>
              ) : null}
            </p>
          ) : (
            <p>No technical check result yet.</p>
          )}
        </section>

        <div>
          <section className="card" aria-labelledby="script-h">
            <h2 id="script-h">Narration and captions</h2>
            <div className="table-wrap">
              <table>
                <thead><tr><th>Scene</th><th>Narration</th><th>Caption</th></tr></thead>
                <tbody>
                  {detail.scenes.map((scene) => (
                    <tr key={scene.sceneId}>
                      <td>{scene.role}</td>
                      <td>{scene.narration}</td>
                      <td>
                        <label className="field">
                          <span className="muted">Caption for {scene.role}</span>
                          <input
                            type="text"
                            value={captions[scene.sceneId] ?? scene.caption}
                            onChange={(event) => setCaptions({ ...captions, [scene.sceneId]: event.target.value })}
                          />
                        </label>
                        <button
                          type="button"
                          className="secondary"
                          disabled={busy || (captions[scene.sceneId] ?? scene.caption) === scene.caption}
                          onClick={() => saveCaption(scene.sceneId)}
                        >
                          Save as new version
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="muted">Disclosure text: {detail.disclosure}</p>
          </section>

          <section className="card" aria-labelledby="claims-h">
            <h2 id="claims-h">Claims used (approved with a source)</h2>
            {detail.claims.length === 0 ? <p>No claims are used in this script.</p> : (
              <ul>{detail.claims.map((claim) => <li key={claim.id}>{claim.text} <span className="muted">— source: {claim.sourceReference}</span></li>)}</ul>
            )}
          </section>

          <section className="card" aria-labelledby="assets-h">
            <h2 id="assets-h">Original product images</h2>
            <div className="assets">
              {detail.productAssets.map((asset, index) => (
                // eslint-disable-next-line @next/next/no-img-element
                <img key={asset.id} src={asset.url} alt={`Original product image ${index + 1}`} />
              ))}
            </div>
            <p className="muted">Compare these with the video. Product shape, colour and logo must not be changed.</p>
          </section>
        </div>
      </div>

      {detail.decision ? (
        <section className="card" aria-labelledby="decision-h">
          <h2 id="decision-h">Decision</h2>
          <p data-testid="decision">
            {detail.decision.decision === "approved" ? "✓ Approved" : "✕ Rejected"} · manifest {shortHash(detail.decision.manifestSha256)}
          </p>
        </section>
      ) : null}

      {reviewable ? (
        <section className="card" aria-labelledby="decide-h">
          <h2 id="decide-h">Your decision</h2>
          {!qaPassed ? <p className="alert bad">This version did not pass technical checks and cannot be approved.</p> : null}
          <fieldset style={{ border: 0, padding: 0 }}>
            <legend>Confirm every point before approving</legend>
            {detail.checklist.map((item) => (
              <label className="check" key={item.key}>
                <input
                  type="checkbox"
                  checked={ticks[item.key] === true}
                  onChange={(event) => setTicked({ ...ticks, [item.key]: event.target.checked })}
                />
                <span>{item.label}</span>
              </label>
            ))}
          </fieldset>
          <label className="field">
            <span>Comment (required to reject)</span>
            <textarea rows={3} value={comment} onChange={(event) => setComment(event.target.value)} />
          </label>
          <p>
            <button
              type="button"
              disabled={busy || !canApprove({ status: detail.status, qaPassed, items: detail.checklist, ticked: ticks })}
              onClick={() => decide(true)}
            >
              Approve this exact version
            </button>{" "}
            <button
              type="button"
              className="danger"
              disabled={busy || !canReject({ status: detail.status, comment })}
              onClick={() => decide(false)}
            >
              Reject
            </button>
          </p>
        </section>
      ) : null}

      {detail.status === "approved" ? <ExportPanel detail={detail} /> : null}
    </>
  );
}
