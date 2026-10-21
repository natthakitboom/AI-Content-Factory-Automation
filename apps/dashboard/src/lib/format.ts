export const NO_DATA = "No data";

export function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined ? NO_DATA : value.toLocaleString("en-US");
}

export function formatPercent(value: number | null | undefined): string {
  return value === null || value === undefined ? NO_DATA : `${(value * 100).toFixed(1)}%`;
}

export function formatMoney(value: number | null | undefined, currency: string): string {
  return value === null || value === undefined ? NO_DATA : `${value.toFixed(2)} ${currency}`;
}

export function shortHash(hash: string | null | undefined): string {
  return hash ? hash.slice(0, 12) : NO_DATA;
}

export function formatTime(iso: string | null | undefined): string {
  if (!iso) {
    return NO_DATA;
  }
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? NO_DATA : date.toLocaleString("en-GB", { hour12: false });
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined) {
    return NO_DATA;
  }
  const gib = bytes / 1024 ** 3;
  return gib >= 1 ? `${gib.toFixed(1)} GiB` : `${(bytes / 1024 ** 2).toFixed(0)} MiB`;
}
