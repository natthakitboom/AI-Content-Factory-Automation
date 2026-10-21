"use client";

import { api } from "@/lib/api";
import { formatMoney, formatTime } from "@/lib/format";
import { useLoad } from "@/lib/useLoad";
import type { CostSummary } from "@/lib/types";

export default function CostsPage() {
  const costs = useLoad(() => api.get<CostSummary>("/api/v1/system/costs"));
  if (costs.error) {
    return <p role="alert" className="alert bad">{costs.error.message}</p>;
  }
  const c = costs.data;
  if (!c) {
    return <p>Loading…</p>;
  }
  const money = (value: number) => formatMoney(value, c.currency);

  return (
    <>
      <h1>Costs</h1>
      <p className="muted">{c.note}</p>
      <div className="grid">
        <div className="card"><h2>Reserved (held)</h2><div className="stat" data-testid="reserved-cost">{money(c.reservedUsd)}</div><p className="muted">Set aside for requests in flight or uncertain. Not spent.</p></div>
        <div className="card"><h2>Spent this month</h2><div className="stat" data-testid="settled-cost">{money(c.settledUsd)}</div><p className="muted">Recorded after a result was delivered.</p></div>
        <div className="card"><h2>Held for unknown requests</h2><div className="stat" data-testid="unknown-cost">{money(c.unknownHeldUsd)}</div><p className="muted">Kept until you reconcile with the provider.</p></div>
        <div className="card"><h2>Remaining this month</h2><div className="stat" data-testid="remaining-cost">{money(c.remainingMonthlyUsd)}</div><p className="muted">Monthly limit {money(c.monthlyBudgetUsd)} · daily limit {money(c.dailyBudgetUsd)}</p></div>
      </div>
      <section className="card">
        <h2>Price snapshot</h2>
        {c.priceSnapshot ? (
          <>
            {c.priceSnapshot.stale ? (
              <p role="alert" className="alert" data-testid="stale-price">Stale price: this snapshot expired {formatTime(c.priceSnapshot.validUntil)}. Paid work is blocked until a verified price exists.</p>
            ) : (
              <p className="alert ok">Current price snapshot, valid until {formatTime(c.priceSnapshot.validUntil)}.</p>
            )}
            <p>{c.priceSnapshot.provider} / {c.priceSnapshot.model}: {money(c.priceSnapshot.unitPriceUsd)} per request, verified {formatTime(c.priceSnapshot.verifiedAt)}.</p>
          </>
        ) : <p data-testid="no-price">No price snapshot has been recorded, so no paid request can be priced.</p>}
      </section>
    </>
  );
}
