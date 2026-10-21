"use client";

import Link from "next/link";
import { useState } from "react";
import { StatusBadge } from "@/components/StatusBadge";
import { api } from "@/lib/api";
import { formatTime } from "@/lib/format";
import { useLoad } from "@/lib/useLoad";
import type { VideoQueueItem } from "@/lib/types";

const FILTERS = [
  { value: "awaiting_review", label: "Awaiting review" },
  { value: "approved", label: "Approved" },
  { value: "rejected", label: "Rejected" },
  { value: "qa_failed", label: "QA failed" },
  { value: "", label: "All" },
];

export default function VideosPage() {
  const [status, setStatus] = useState("awaiting_review");
  const videos = useLoad(
    () => api.get<VideoQueueItem[]>(`/api/v1/videos?status=${encodeURIComponent(status)}`),
    [status],
  );

  return (
    <>
      <h1>Review queue</h1>
      <label className="field">
        <span>Show</span>
        <select value={status} onChange={(event) => setStatus(event.target.value)}>
          {FILTERS.map((filter) => (
            <option key={filter.value} value={filter.value}>{filter.label}</option>
          ))}
        </select>
      </label>
      {videos.error ? <p role="alert" className="alert bad">{videos.error.message}</p> : null}
      {videos.data && videos.data.length === 0 ? <p className="card">No videos in this view.</p> : null}
      {videos.data && videos.data.length > 0 ? (
        <div className="card table-wrap">
          <table>
            <thead><tr><th>Product</th><th>Version</th><th>Status</th><th>Created</th><th /></tr></thead>
            <tbody>
              {videos.data.map((video) => (
                <tr key={video.id}>
                  <td>{video.title}</td>
                  <td>v{video.versionNo}</td>
                  <td><StatusBadge status={video.status} /></td>
                  <td>{formatTime(video.createdAt)}</td>
                  <td><Link href={`/videos/${video.id}`}>Open</Link></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </>
  );
}
