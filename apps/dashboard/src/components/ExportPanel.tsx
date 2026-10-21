"use client";

import { useState, type FormEvent } from "react";
import { api, ApiError } from "@/lib/api";
import { formatTime, shortHash } from "@/lib/format";
import { useLoad } from "@/lib/useLoad";
import type { ExportPackageView, PublicationView, VideoReviewDetail } from "@/lib/types";

const POSTING_STEPS = [
  "Turn on the platform's advertising / paid-promotion label where it applies.",
  "Turn on the affiliate or commercial-content disclosure where it applies.",
  "Add the product link yourself and open it to confirm it works.",
  "Confirm the offer, price and stock on the product page before posting.",
];

export function ExportPanel({ detail }: { detail: VideoReviewDetail }) {
  const packages = useLoad(() => api.get<ExportPackageView[]>(`/api/v1/videos/${detail.id}/exports`), [detail.id]);
  const publications = useLoad(() => api.get<PublicationView[]>(`/api/v1/videos/${detail.id}/publications`), [detail.id]);
  const [message, setMessage] = useState<{ kind: "ok" | "bad"; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  async function createExport() {
    setBusy(true);
    setMessage(null);
    try {
      await api.post(`/api/v1/videos/${detail.id}/exports`, { channelId: detail.channelId });
      setMessage({ kind: "ok", text: "Package created. Download it, check the video, then post it yourself." });
      await packages.reload();
    } catch (caught) {
      setMessage({ kind: "bad", text: caught instanceof ApiError ? `${caught.message} (${caught.code})` : "Export failed." });
    } finally {
      setBusy(false);
    }
  }

  async function record(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setBusy(true);
    setMessage(null);
    try {
      await api.post(`/api/v1/videos/${detail.id}/publications`, {
        channelId: detail.channelId,
        postUrl: String(form.get("postUrl") ?? ""),
        productLinkStatus: String(form.get("productLinkStatus") ?? "unknown"),
        evidenceReference: String(form.get("evidenceReference") ?? "") || null,
      });
      setMessage({ kind: "ok", text: "Recorded. This only logs a post you made yourself." });
      await publications.reload();
    } catch (caught) {
      setMessage({ kind: "bad", text: caught instanceof ApiError ? `${caught.message} (${caught.code})` : "Could not record." });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="card" aria-labelledby="export-h" data-testid="export-panel">
      <h2 id="export-h">Export for manual posting</h2>
      <p className="alert" role="note">Not published. This system never posts for you and never claims a post or a product link exists.</p>
      {message ? <p role="status" className={`alert ${message.kind}`} data-testid="export-message">{message.text}</p> : null}
      <button type="button" disabled={busy} onClick={createExport}>Create export package</button>

      {packages.data && packages.data.length > 0 ? (
        <ul data-testid="export-list">
          {packages.data.map((item) => (
            <li key={item.id}>
              <a href={item.downloadUrl} download>Download ZIP</a>{" "}
              <span className="muted mono">{shortHash(item.packageHash)} · {formatTime(item.createdAt)}</span>
            </li>
          ))}
        </ul>
      ) : <p className="muted">No package yet.</p>}

      <h3>Before you post</h3>
      <ul>{POSTING_STEPS.map((step) => <li key={step}>{step}</li>)}</ul>

      <h3>After you post it yourself</h3>
      <form onSubmit={record}>
        <label className="field"><span>Post URL (https)</span><input name="postUrl" type="url" required /></label>
        <label className="field">
          <span>Product link status</span>
          <select name="productLinkStatus" defaultValue="unknown">
            <option value="unknown">Unknown (not checked)</option>
            <option value="verified">Verified by me</option>
            <option value="broken">Broken</option>
            <option value="not_applicable">Not applicable</option>
          </select>
        </label>
        <label className="field"><span>Evidence reference (needed unless unknown)</span><input name="evidenceReference" type="text" /></label>
        <button type="submit" disabled={busy}>Record my post</button>
      </form>
      {publications.data && publications.data.length > 0 ? (
        <ul data-testid="publication-list">
          {publications.data.map((item) => (
            <li key={item.id}>{item.postUrl} — link: {item.productLinkStatus}</li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
