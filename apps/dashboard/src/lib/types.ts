// Hand-written mirror of the API's JSON contracts (camelCase). The API does not publish an
// OpenAPI document yet, so these types are the single TypeScript copy of each DTO.

export interface ChecklistItem {
  key: string;
  label: string;
}

export interface ReviewScene {
  sceneId: string;
  role: string;
  narration: string;
  caption: string;
  claimIds: string[];
}

export interface ReviewClaim {
  id: string;
  text: string;
  sourceReference: string;
}

export interface ReviewAsset {
  id: string;
  mime: string;
  url: string;
}

export interface ReviewDecisionView {
  decision: "approved" | "rejected";
  reviewerId: string;
  decidedAt: string;
  manifestSha256: string;
}

export interface QaIssue {
  code: string;
  path: string;
  message: string;
}

export interface QaReport {
  passed: boolean;
  errors?: QaIssue[];
  warnings?: QaIssue[];
}

export interface VideoReviewDetail {
  id: string;
  revisionId: string;
  campaignId: string;
  channelId: string;
  versionNo: number;
  status: string;
  manifestSha256: string | null;
  mediaUrl: string | null;
  title: string;
  disclosure: string;
  scenes: ReviewScene[];
  claims: ReviewClaim[];
  productAssets: ReviewAsset[];
  usesAiMotion: boolean;
  aiMotionWarning: string | null;
  cost: { reservedUsd: number; settledUsd: number; currency: string };
  qa: QaReport | null;
  checklist: ChecklistItem[];
  decision: ReviewDecisionView | null;
}

export interface VideoQueueItem {
  id: string;
  versionNo: number;
  status: string;
  title: string;
  createdAt: string;
}

export interface ExportPackageView {
  id: string;
  channelId: string;
  packageHash: string;
  createdAt: string;
  assetId: string;
  downloadUrl: string;
}

export interface PublicationView {
  id: string;
  channelId: string;
  postUrl: string;
  productLinkStatus: string;
  evidenceReference: string | null;
  publishedAt: string;
}

export interface ModeInfo {
  banner: string;
  mock: boolean;
  text: string;
  video: string;
  voice: string;
}

export interface SystemStatus {
  hasData: boolean;
  paused: boolean;
  paidLanesPaused: boolean;
  queued: number;
  running: number;
  pausedJobs: number;
  blocked: number;
  failed: number;
  awaitingReview: number;
  workerHeartbeat: { lastSeenAt: string | null; stale: boolean };
}

export interface JobView {
  id: string;
  jobType: string;
  lane: string;
  status: string;
  attempts: number;
  maxAttempts: number;
  lastErrorCode: string | null;
  explanation: string | null;
  canRetry: boolean;
  needsReconciliation: boolean;
  runAfter: string;
  createdAt: string;
  updatedAt: string;
}

export interface JobDetail {
  id: string;
  jobType: string;
  status: string;
  lastErrorCode: string | null;
  explanation: string | null;
  canRetry: boolean;
  needsReconciliation: boolean;
  events: { eventType: string; occurredAt: string; details: unknown }[];
  attempts: { attemptNo: number; startedAt: string; endedAt: string | null; errorCode: string | null }[];
}

export interface CostSummary {
  currency: string;
  reservedUsd: number;
  settledUsd: number;
  unknownHeldUsd: number;
  monthlyBudgetUsd: number;
  dailyBudgetUsd: number;
  remainingMonthlyUsd: number;
  priceSnapshot: {
    provider: string;
    model: string;
    unitPriceUsd: number;
    verifiedAt: string;
    validUntil: string;
    stale: boolean;
  } | null;
  note: string;
}

export interface Diagnostics {
  lmStudio: { status: string; detail?: string; httpStatus?: number };
  selectedModelId: string | null;
  media: {
    status: string;
    hardwareEncoder?: boolean;
    softwareEncoder?: boolean;
    subtitlesFilter?: boolean;
    detail?: string;
  };
  disk: { freeBytes: number | null; totalBytes: number | null };
  thermal: { status: string; detail: string };
  configured: { falKey: boolean; lmStudioToken: boolean; captionFontFile: boolean };
}

export interface MetricsOverview {
  views: number | null;
  productClicks: number | null;
  orders: number | null;
  ctr: number | null;
  cvr: number | null;
  commission: { currency: string; pending: number | null; settled: number | null }[];
  publications: number;
  earliestAsOf: string | null;
  latestAsOf: string | null;
  sources: string[];
}

export interface AnalyticsOverview {
  hasData: boolean;
  bySource: { source: string; overview: MetricsOverview }[];
  note: string;
  definitions: Record<string, string>;
}

export interface ImportReport {
  inserted: number;
  updated: number;
  unchanged: number;
  rejected: number;
  issues: { row: number; code: string; message: string }[];
}
