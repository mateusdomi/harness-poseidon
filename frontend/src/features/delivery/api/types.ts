import { z } from 'zod';

/**
 * Contratos da Central de Entregas (`/api/v1/deliveries/*`), espelhando o
 * OpenAPI já publicado pelo backend (DEL-01..10). A UI CONSOME estas rotas —
 * não há backend novo aqui.
 *
 * Nota de contrato: inteiros (int32/int64) e números (double) chegam como
 * número OU string no OpenAPI; normalizamos sempre para `number`.
 */

const int = z.union([z.number().int(), z.string().regex(/^-?\d+$/)]).transform(Number);
const num = z.union([z.number(), z.string().regex(/^-?\d+(\.\d+)?$/)]).transform(Number);

/* ------------------------------------------------------------------ */
/* Sinais, previsão                                                    */
/* ------------------------------------------------------------------ */

export const attentionSignalSchema = z.object({
  code: z.string(),
  severity: z.string(),
  detail: z.string(),
});
export type AttentionSignal = z.infer<typeof attentionSignalSchema>;

export const forecastBasisSchema = z.object({
  signal: z.string(),
  detail: z.string(),
});
export type ForecastBasis = z.infer<typeof forecastBasisSchema>;

export const deliveryForecastSchema = z.object({
  id: z.string().nullable(),
  forecastDate: z.string().nullable(),
  confidence: z.string(),
  confidencePercent: int,
  hasSufficientEvidence: z.boolean(),
  basis: z.array(forecastBasisSchema),
  createdAt: z.string().nullable(),
});
export type DeliveryForecast = z.infer<typeof deliveryForecastSchema>;

export const deliveryForecastHistorySchema = z.object({
  deliveryId: z.string(),
  latest: deliveryForecastSchema.nullable(),
  history: z.array(deliveryForecastSchema),
});
export type DeliveryForecastHistory = z.infer<typeof deliveryForecastHistorySchema>;

/* ------------------------------------------------------------------ */
/* Portfólio (DEL-01)                                                  */
/* ------------------------------------------------------------------ */

export const deliverySummarySchema = z.object({
  deliveryId: z.string(),
  projectId: z.string(),
  name: z.string(),
  key: z.string(),
  health: z.string(),
  predictability: z.string(),
  owner: z.string().nullable(),
  committedDate: z.string().nullable(),
  forecastDate: z.string().nullable(),
  forecastConfidence: z.string().nullable(),
  milestonesTotal: int,
  milestonesDone: int,
  openTaskCount: int,
  blockedTaskCount: int,
  lastActivityAt: z.string(),
  attentionSignals: z.array(attentionSignalSchema),
});
export type DeliverySummary = z.infer<typeof deliverySummarySchema>;

export const deliveryPortfolioSchema = z.object({
  view: z.string(),
  total: int,
  deliveries: z.array(deliverySummarySchema),
});
export type DeliveryPortfolio = z.infer<typeof deliveryPortfolioSchema>;

export const deliveryPlanningSchema = z.object({
  deliveryId: z.string(),
  ownerAgentId: z.string(),
  ownerName: z.string(),
  committedDate: z.string(),
  updatedTaskCount: int,
  updatedAt: z.string(),
});
export type DeliveryPlanning = z.infer<typeof deliveryPlanningSchema>;
export interface DeliveryPlanningInput {
  ownerAgentId: string;
  committedDate: string;
}

/* ------------------------------------------------------------------ */
/* Entrega 360 (DEL-02)                                                */
/* ------------------------------------------------------------------ */

export const executiveSummarySchema = z.object({
  name: z.string(),
  key: z.string(),
  criticality: z.string(),
  health: z.string(),
  predictability: z.string(),
  owner: z.string().nullable(),
  milestonesTotal: int,
  milestonesDone: int,
  openTaskCount: int,
  blockedTaskCount: int,
  committedDate: z.string().nullable(),
  forecastDate: z.string().nullable(),
  lastActivityAt: z.string(),
  attentionSignalCount: int,
});

export const planMilestonesSchema = z.object({
  milestonesTotal: int,
  milestonesDone: int,
  committedDate: z.string().nullable(),
  forecast: deliveryForecastSchema,
  forecastHistory: z.array(deliveryForecastSchema),
});

export const technicalHealthIndicatorSchema = z.object({
  key: z.string(),
  label: z.string(),
  status: z.string(),
  value: z.string(),
  detail: z.string(),
});
export type TechnicalHealthIndicator = z.infer<typeof technicalHealthIndicatorSchema>;

export const technicalHealthSchema = z.object({
  indicators: z.array(technicalHealthIndicatorSchema),
});

export const riskDependencySchema = z.object({
  risks: z.array(attentionSignalSchema),
  openDependencies: int,
  blockedTaskCount: int,
});

export const decisionSchema = z.object({
  taskId: z.string(),
  state: z.string(),
  detail: z.string(),
  resolved: z.boolean(),
});
export type DeliveryDecision = z.infer<typeof decisionSchema>;

export const decisionsSchema = z.object({
  total: int,
  open: int,
  decisions: z.array(decisionSchema),
});

export const documentationChecklistItemSchema = z.object({
  kind: z.string(),
  label: z.string(),
  present: z.boolean(),
  state: z.string().nullable(),
});
export type DocumentationChecklistItem = z.infer<typeof documentationChecklistItemSchema>;

export const documentationSchema = z.object({
  expected: int,
  present: int,
  checklist: z.array(documentationChecklistItemSchema),
});

export const featureMetricSchema = z.object({
  featureId: z.string(),
  taskCount: int,
  attemptCount: int,
  successCount: int,
  failureCount: int,
  totalCostUsd: num,
  totalTokensInput: int,
  totalTokensOutput: int,
  totalDurationMs: int,
});
export type DeliveryFeatureMetric = z.infer<typeof featureMetricSchema>;

export const valueMetricsSchema = z.object({
  features: z.array(featureMetricSchema),
  totalCostUsd: num,
  totalTasks: int,
});

export const delivery360Schema = z.object({
  deliveryId: z.string(),
  projectId: z.string(),
  executiveSummary: executiveSummarySchema,
  planAndMilestones: planMilestonesSchema,
  technicalHealth: technicalHealthSchema,
  risksAndDependencies: riskDependencySchema,
  decisions: decisionsSchema,
  documentation: documentationSchema,
  valueAndMetrics: valueMetricsSchema,
});
export type Delivery360 = z.infer<typeof delivery360Schema>;

/* ------------------------------------------------------------------ */
/* Métricas DORA (DEL-06)                                              */
/* ------------------------------------------------------------------ */

export const deliveryMetricSchema = z.object({
  key: z.string(),
  label: z.string(),
  category: z.string(),
  measured: z.boolean(),
  value: z.string().nullable(),
  unit: z.string().nullable(),
  basis: z.string(),
});
export type DeliveryMetric = z.infer<typeof deliveryMetricSchema>;

export const deliveryMetricsSchema = z.object({
  deliveryId: z.string(),
  generatedAt: z.string(),
  dora: z.array(deliveryMetricSchema),
  own: z.array(deliveryMetricSchema),
});
export type DeliveryMetrics = z.infer<typeof deliveryMetricsSchema>;

/* ------------------------------------------------------------------ */
/* Central de Relatórios (DEL-04/05/10)                                */
/* ------------------------------------------------------------------ */

export const reportFieldSchema = z.object({ label: z.string(), value: z.string() });
export const reportTableSchema = z.object({
  columns: z.array(z.string()),
  rows: z.array(z.array(z.string())),
});
export const reportSectionSchema = z.object({
  key: z.string(),
  title: z.string(),
  fields: z.array(reportFieldSchema),
  table: reportTableSchema.nullable(),
});
export const reportDocumentSchema = z.object({
  type: z.string(),
  title: z.string(),
  deliveryId: z.string(),
  projectKey: z.string(),
  audience: z.string(),
  classification: z.string(),
  generatedAt: z.string(),
  sections: z.array(reportSectionSchema),
});
export type ReportDocument = z.infer<typeof reportDocumentSchema>;

export const deliveryReportSchema = z.object({
  id: z.string(),
  deliveryId: z.string(),
  projectId: z.string(),
  type: z.string(),
  format: z.string(),
  status: z.string(),
  audience: z.string(),
  classification: z.string(),
  version: int,
  contentType: z.string(),
  available: z.boolean(),
  content: z.string().nullable(),
  reason: z.string().nullable(),
  approvedBy: z.string().nullable(),
  approvedAt: z.string().nullable(),
  sentAt: z.string().nullable(),
  createdAt: z.string(),
  document: reportDocumentSchema.nullable(),
});
export type DeliveryReport = z.infer<typeof deliveryReportSchema>;

export const deliveryReportSummarySchema = z.object({
  id: z.string(),
  deliveryId: z.string(),
  type: z.string(),
  format: z.string(),
  status: z.string(),
  audience: z.string(),
  classification: z.string(),
  version: int,
  available: z.boolean(),
  approvedBy: z.string().nullable(),
  approvedAt: z.string().nullable(),
  sentAt: z.string().nullable(),
  createdAt: z.string(),
});
export type DeliveryReportSummary = z.infer<typeof deliveryReportSummarySchema>;

export const deliveryReportListSchema = z.object({
  deliveryId: z.string(),
  total: int,
  reports: z.array(deliveryReportSummarySchema),
});
export type DeliveryReportList = z.infer<typeof deliveryReportListSchema>;

export const reportSendReceiptSchema = z.object({
  reportId: z.string(),
  version: int,
  channel: z.string(),
  sentBy: z.string(),
  sentAt: z.string(),
  result: z.string(),
  report: deliveryReportSchema,
});
export type ReportSendReceipt = z.infer<typeof reportSendReceiptSchema>;

export interface GenerateReportInput {
  type: string;
  format: string;
  audience?: string;
  classification?: string;
}
export interface ApproveReportInput {
  approvedBy?: string;
}
export interface SendReportInput {
  channel: string;
  recipientReference: string;
}

/* ------------------------------------------------------------------ */
/* Daily Copilot (DEL-03)                                              */
/* ------------------------------------------------------------------ */

export const dailySnapshotSchema = z.object({
  health: z.string(),
  predictability: z.string(),
  openTaskCount: int,
  blockedTaskCount: int,
  milestonesTotal: int,
  milestonesDone: int,
  forecast: deliveryForecastSchema,
});

export const dailyChangeSchema = z.object({ code: z.string(), detail: z.string() });
export const dailyQuestionSchema = z.object({ topic: z.string(), question: z.string() });

export const dailyBriefingSchema = z.object({
  deliveryId: z.string(),
  generatedAt: z.string(),
  isFirstDaily: z.boolean(),
  lastDailyAt: z.string().nullable(),
  snapshot: dailySnapshotSchema,
  changesSinceLast: z.array(dailyChangeSchema),
  itemsNeedingAttention: z.array(attentionSignalSchema),
  recommendedQuestions: z.array(dailyQuestionSchema),
});
export type DailyBriefing = z.infer<typeof dailyBriefingSchema>;

export const dailyCaptureSchema = z.object({
  id: z.string(),
  deliveryId: z.string(),
  kind: z.string(),
  note: z.string(),
  capturedBy: z.string(),
  createdAt: z.string(),
});
export type DailyCapture = z.infer<typeof dailyCaptureSchema>;

export const dailyCaptureKindCountSchema = z.object({ kind: z.string(), count: int });

export const dailySummarySchema = z.object({
  deliveryId: z.string(),
  generatedAt: z.string(),
  sessionSince: z.string().nullable(),
  totalCaptures: int,
  byKind: z.array(dailyCaptureKindCountSchema),
  captures: z.array(dailyCaptureSchema),
  snapshot: dailySnapshotSchema,
  createdPoCards: z.boolean(),
});
export type DailySummary = z.infer<typeof dailySummarySchema>;

export interface DailyCaptureInput {
  kind: string;
  note: string;
  capturedBy?: string;
}

/** As marcações tipadas válidas na daily (DEL-03), espelhando o backend. */
export const DAILY_CAPTURE_KINDS = [
  'access',
  'dependency',
  'decision',
  'deadline',
  'scope',
  'doc',
  'risk',
] as const;

/** Formatos de saída dos relatórios (DEL-04). */
export const REPORT_FORMATS = [
  'markdown',
  'html',
  'csv',
  'json',
  'pdf',
  'pptx',
  'word',
  'zip',
] as const;

/** Tipos de relatório (DEL-04/05). */
export const REPORT_TYPES = [
  'weekly_executive_status',
  'milestone_report',
  'homologation_readiness',
  'production_readiness',
  'closure_dossier',
] as const;
