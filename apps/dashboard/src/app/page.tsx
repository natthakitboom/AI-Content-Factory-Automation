"use client";

import Link from "next/link";
import { api } from "@/lib/api";
import { formatMoney, formatTime } from "@/lib/format";
import { useLoad } from "@/lib/useLoad";
import type { CostSummary, SystemStatus } from "@/lib/types";

export default function OverviewPage() {
  const status = useLoad(() => api.get<SystemStatus>("/api/v1/system/status"));
  const costs = useLoad(() => api.get<CostSummary>("/api/v1/system/costs"));

  if (status.error) {
    return <p role="alert" className="alert bad">Could not load status: {status.error.message}</p>;
  }
  const s = status.data;
  if (!s) {
    return <p>Loading…</p>;
  }

  return (
    <>
      <h1>Overview</h1>
      {s.paused ? (
        <p className="alert" role="status" data-testid="paused-notice">
          Production is paused. New local and cloud-submit work is withheld; cloud requests already sent keep running.
        </p>
      ) : null}
      {s.paidLanesPaused ? (
        <p className="alert bad" role="status">
          Paid work is paused after a restore. Reconcile the cost ledger with the provider before resuming it.
        </p>
      ) : null}
      {!s.hasData ? (
        <p className="card" data-testid="empty-state">
          Nothing has run yet. When a campaign run starts, its jobs, review queue and costs appear here. Nothing is shown until it is real.
        </p>
      ) : null}
      <div className="grid">
        <div className="card">
          <h2>Review queue</h2>
          <div className="stat" data-testid="awaiting-review">{s.awaitingReview}</div>
          <Link href="/videos">Open review queue</Link>
        </div>
        <div className="card">
          <h2>Jobs</h2>
          <p>Queued {s.queued} · Running {s.running} · Paused {s.pausedJobs}</p>
          <p>Blocked {s.blocked} · Failed {s.failed}</p>
          <Link href="/jobs">Open jobs</Link>
        </div>
        <div className="card">
          <h2>Money</h2>
          {costs.data ? (
            <>
              <p data-testid="reserved-cost">Reserved (held): {formatMoney(costs.data.reservedUsd, costs.data.currency)}</p>
              <p data-testid="settled-cost">Spent this month: {formatMoney(costs.data.settledUsd, costs.data.currency)}</p>
              <Link href="/costs">Open costs</Link>
            </>
          ) : (
            <p>Loading…</p>
          )}
        </div>
        <div className="card">
          <h2>Worker</h2>
          <p data-testid="worker-heartbeat">
            {s.workerHeartbeat.lastSeenAt
              ? `Last seen ${formatTime(s.workerHeartbeat.lastSeenAt)}${s.workerHeartbeat.stale ? " — stale" : ""}`
              : "No heartbeat recorded — the worker is not running."}
          </p>
        </div>
      </div>
    </>
  );
}
