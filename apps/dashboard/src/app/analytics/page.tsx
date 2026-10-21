"use client";

import { useState, type FormEvent } from "react";
import { api, ApiError } from "@/lib/api";
import { formatCount, formatMoney, formatPercent, formatTime } from "@/lib/format";
import { useLoad } from "@/lib/useLoad";
import type { AnalyticsOverview, ImportReport } from "@/lib/types";

export default function AnalyticsPage() {
  const overview = useLoad(() => api.get<AnalyticsOverview>("/api/v1/analytics/overview"));
  const [report, setReport] = useState<ImportReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const column = (name: string) => String(form.get(name) ?? "").trim() || null;
    const mapping = {
      source: String(form.get("source") ?? "").trim(),
      currency: column("currency"),
      asOf: form.get("asOf") ? `${String(form.get("asOf"))}T00:00:00Z` : null,
      asOfColumn: column("asOfColumn"),
      publicationColumn: String(form.get("publicationColumn") ?? "").trim(),
      publicationKind: String(form.get("publicationKind") ?? "post_url"),
      viewsColumn: column("viewsColumn"),
      productClicksColumn: column("productClicksColumn"),
      ordersColumn: column("ordersColumn"),
      commissionPendingColumn: column("commissionPendingColumn"),
      commissionSettledColumn: column("commissionSettledColumn"),
      commissionReturnedColumn: column("commissionReturnedColumn"),
    };
    const body = new FormData();
    body.set("file", form.get("file") as Blob);
    body.set("mapping", JSON.stringify(mapping));
    setBusy(true);
    setError(null);
    try {
      setReport(await api.postForm<ImportReport>("/api/v1/metrics/imports", body));
      await overview.reload();
    } catch (caught) {
      setError(caught instanceof ApiError ? `${caught.message} (${caught.code})` : "Import failed.");
    } finally {
      setBusy(false);
    }
  }

  const o = overview.data;
  return (
    <>
      <h1>Analytics</h1>
      <p className="muted">Numbers come only from CSV files you import. A blank means no data, not zero.</p>
      {o && !o.hasData ? <p className="card" data-testid="no-metrics">No metrics have been imported yet.</p> : null}
      {o?.bySource.map(({ source, overview: m }) => (
        <section className="card" key={source} data-testid="metrics-source">
          <h2>{source}</h2>
          <p className="muted">{m.publications} posts · figures as of {formatTime(m.latestAsOf)}</p>
          <div className="grid">
            <div><h3>Views</h3><div className="stat">{formatCount(m.views)}</div></div>
            <div><h3>Product clicks</h3><div className="stat">{formatCount(m.productClicks)}</div></div>
            <div><h3>Orders</h3><div className="stat">{formatCount(m.orders)}</div></div>
            <div><h3>Click rate</h3><div className="stat">{formatPercent(m.ctr)}</div></div>
            <div><h3>Order rate</h3><div className="stat">{formatPercent(m.cvr)}</div></div>
          </div>
          {m.commission.map((row) => (
            <p key={row.currency}>Commission {row.currency}: pending {formatMoney(row.pending, row.currency)} · settled {formatMoney(row.settled, row.currency)}</p>
          ))}
        </section>
      ))}
      {o ? (
        <details className="card">
          <summary>What these numbers mean</summary>
          <p>{o.note}</p>
          <ul>{Object.entries(o.definitions).map(([key, text]) => <li key={key}><strong>{key}</strong>: {text}</li>)}</ul>
        </details>
      ) : null}

      <section className="card">
        <h2>Import a CSV</h2>
        {error ? <p role="alert" className="alert bad">{error}</p> : null}
        <form onSubmit={submit}>
          <label className="field"><span>CSV file (up to 10 MiB)</span><input name="file" type="file" accept=".csv,text/csv" required /></label>
          <label className="field"><span>Source name</span><input name="source" type="text" required placeholder="tiktok-export" /></label>
          <label className="field"><span>Which column identifies the post?</span><input name="publicationColumn" type="text" required /></label>
          <label className="field"><span>That column holds</span>
            <select name="publicationKind" defaultValue="post_url"><option value="post_url">Post URL</option><option value="external_post_id">Post ID</option></select>
          </label>
          <label className="field"><span>As-of date for every row</span><input name="asOf" type="date" /></label>
          <label className="field"><span>…or the column holding the date</span><input name="asOfColumn" type="text" /></label>
          <label className="field"><span>Currency (needed for commission)</span><input name="currency" type="text" maxLength={3} placeholder="THB" /></label>
          <label className="field"><span>Views column</span><input name="viewsColumn" type="text" /></label>
          <label className="field"><span>Product clicks column</span><input name="productClicksColumn" type="text" /></label>
          <label className="field"><span>Orders column</span><input name="ordersColumn" type="text" /></label>
          <label className="field"><span>Pending commission column</span><input name="commissionPendingColumn" type="text" /></label>
          <label className="field"><span>Settled commission column</span><input name="commissionSettledColumn" type="text" /></label>
          <label className="field"><span>Returned commission column (reduces settled)</span><input name="commissionReturnedColumn" type="text" /></label>
          <button type="submit" disabled={busy}>{busy ? "Importing…" : "Import"}</button>
        </form>
        {report ? (
          <div data-testid="import-report" role="status">
            <p>Added {report.inserted} · updated {report.updated} · unchanged {report.unchanged} · rejected {report.rejected}</p>
            {report.issues.length > 0 ? <ul>{report.issues.map((issue, index) => <li key={index}>Row {issue.row}: {issue.message} ({issue.code})</li>)}</ul> : null}
          </div>
        ) : null}
      </section>
    </>
  );
}
