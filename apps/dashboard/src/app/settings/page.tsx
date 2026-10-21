"use client";

import { useState } from "react";
import { api, ApiError } from "@/lib/api";
import { formatBytes, formatTime } from "@/lib/format";
import { useLoad } from "@/lib/useLoad";
import type { Diagnostics, ModeInfo, SystemStatus } from "@/lib/types";

function yesNo(value: boolean | undefined): string {
  return value === undefined ? "not checked" : value ? "yes" : "no";
}

export default function SettingsPage() {
  const status = useLoad(() => api.get<SystemStatus>("/api/v1/system/status"));
  const mode = useLoad(() => api.get<ModeInfo>("/api/v1/system/mode"));
  const diagnostics = useLoad(() => api.get<Diagnostics>("/api/v1/system/diagnostics"));
  const [message, setMessage] = useState<string | null>(null);

  async function toggle(pause: boolean) {
    setMessage(null);
    try {
      await api.post(pause ? "/api/v1/system/pause" : "/api/v1/system/resume");
      await status.reload();
    } catch (caught) {
      setMessage(caught instanceof ApiError ? caught.message : "The change failed.");
    }
  }

  const d = diagnostics.data;
  return (
    <>
      <h1>Settings and diagnostics</h1>
      {message ? <p role="alert" className="alert bad">{message}</p> : null}

      <section className="card">
        <h2>Production</h2>
        <p data-testid="pause-state">{status.data ? (status.data.paused ? "✕ Paused — new local and cloud-submit work is withheld." : "✓ Running") : "Loading…"}</p>
        <p className="muted">Pausing never cancels a cloud request already sent and is not a refund.</p>
        {status.data?.paused ? (
          <button type="button" onClick={() => toggle(false)}>Resume production</button>
        ) : (
          <button type="button" className="danger" onClick={() => toggle(true)}>Pause production</button>
        )}
      </section>

      <section className="card">
        <h2>Providers</h2>
        {mode.data ? <p data-testid="providers">Text: {mode.data.text} · Video: {mode.data.video} · Voice: {mode.data.voice}</p> : <p>Loading…</p>}
        <p className="muted">Credentials are never shown. Only whether one is configured appears below.</p>
      </section>

      <section className="card" data-testid="diagnostics">
        <h2>Readiness</h2>
        {!d ? <p>{diagnostics.error ? diagnostics.error.message : "Checking…"}</p> : (
          <table>
            <tbody>
              <tr><th scope="row">LM Studio</th><td>{d.lmStudio.status}{d.lmStudio.detail ? ` — ${d.lmStudio.detail}` : ""}</td></tr>
              <tr><th scope="row">Selected model</th><td>{d.selectedModelId ?? "not set"}</td></tr>
              <tr><th scope="row">FFmpeg</th><td>{d.media.status === "checked"
                ? `hardware encoder: ${yesNo(d.media.hardwareEncoder)}, software encoder: ${yesNo(d.media.softwareEncoder)}, subtitles filter: ${yesNo(d.media.subtitlesFilter)}`
                : `${d.media.status}${d.media.detail ? ` — ${d.media.detail}` : ""}`}</td></tr>
              <tr><th scope="row">Disk free</th><td>{formatBytes(d.disk.freeBytes)} of {formatBytes(d.disk.totalBytes)}</td></tr>
              <tr><th scope="row">Worker heartbeat</th><td>{status.data ? (status.data.workerHeartbeat.lastSeenAt ? `${formatTime(status.data.workerHeartbeat.lastSeenAt)}${status.data.workerHeartbeat.stale ? " (stale)" : ""}` : "none — worker not running") : "…"}</td></tr>
              <tr><th scope="row">Temperature</th><td>{d.thermal.status} — {d.thermal.detail}</td></tr>
              <tr><th scope="row">Configured</th><td>fal key: {yesNo(d.configured.falKey)} · LM Studio token: {yesNo(d.configured.lmStudioToken)} · caption font: {yesNo(d.configured.captionFontFile)}</td></tr>
            </tbody>
          </table>
        )}
      </section>
    </>
  );
}
