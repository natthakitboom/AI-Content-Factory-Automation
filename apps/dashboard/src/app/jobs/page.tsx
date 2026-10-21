"use client";

import { Fragment, useState } from "react";
import { StatusBadge } from "@/components/StatusBadge";
import { api } from "@/lib/api";
import { formatTime } from "@/lib/format";
import { useLoad } from "@/lib/useLoad";
import type { JobDetail, JobView } from "@/lib/types";

const STATUSES = ["", "running", "queued", "paused", "blocked", "failed", "succeeded"];

function Detail({ id }: { id: string }) {
  const detail = useLoad(() => api.get<JobDetail>(`/api/v1/system/jobs/${id}`), [id]);
  if (!detail.data) {
    return <p>{detail.error ? detail.error.message : "Loading…"}</p>;
  }
  return (
    <div data-testid="job-detail">
      <h3>Timeline</h3>
      {detail.data.events.length === 0 ? <p className="muted">No events recorded.</p> : (
        <ol>{detail.data.events.map((event, index) => <li key={index}>{formatTime(event.occurredAt)} — {event.eventType}</li>)}</ol>
      )}
      <h3>Attempts</h3>
      {detail.data.attempts.length === 0 ? <p className="muted">No attempts recorded.</p> : (
        <ol>{detail.data.attempts.map((attempt) => (
          <li key={attempt.attemptNo}>#{attempt.attemptNo} started {formatTime(attempt.startedAt)}{attempt.errorCode ? ` — ${attempt.errorCode}` : ""}</li>
        ))}</ol>
      )}
    </div>
  );
}

export default function JobsPage() {
  const [status, setStatus] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const jobs = useLoad(() => api.get<JobView[]>(`/api/v1/system/jobs?limit=100&status=${encodeURIComponent(status)}`), [status]);

  return (
    <>
      <h1>Jobs</h1>
      <label className="field">
        <span>Status</span>
        <select value={status} onChange={(event) => setStatus(event.target.value)}>
          {STATUSES.map((value) => <option key={value} value={value}>{value || "All"}</option>)}
        </select>
      </label>
      <p className="muted">
        Requests whose cloud state is unknown are never retried from here. Reconcile them with the CLI commands
        <code> reconcile-attach</code> or <code>reconcile-no-charge</code>.
      </p>
      {jobs.error ? <p role="alert" className="alert bad">{jobs.error.message}</p> : null}
      {jobs.data && jobs.data.length === 0 ? <p className="card" data-testid="no-jobs">No jobs have been recorded.</p> : null}
      {jobs.data && jobs.data.length > 0 ? (
        <div className="card table-wrap">
          <table>
            <thead><tr><th>Job</th><th>Status</th><th>Why</th><th>Updated</th><th /></tr></thead>
            <tbody>
              {jobs.data.map((job) => (
                <Fragment key={job.id}>
                  <tr data-testid="job-row" data-job-id={job.id}>
                    <td>{job.jobType}<div className="muted">{job.lane}</div></td>
                    <td><StatusBadge status={job.status} /></td>
                    <td>
                      {job.explanation ?? <span className="muted">—</span>}
                      {job.needsReconciliation ? <div><strong>Needs reconciliation</strong></div> : null}
                    </td>
                    <td>{formatTime(job.updatedAt)}</td>
                    <td>
                      <button type="button" className="secondary" aria-expanded={open === job.id} onClick={() => setOpen(open === job.id ? null : job.id)}>
                        {open === job.id ? "Hide" : "Details"}
                      </button>
                    </td>
                  </tr>
                  {open === job.id ? <tr><td colSpan={5}><Detail id={job.id} /></td></tr> : null}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      ) : null}
    </>
  );
}
