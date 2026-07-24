import { z } from 'zod';

/**
 * Contratos do Architecture Hub (`/api/v1/architecture/*`), espelhando o
 * OpenAPI já publicado pelo backend (ARC-01/02/03/06/07/08/10). O Studio
 * (ARC-04) CONSOME estas rotas — não há backend novo aqui.
 *
 * Observação de contrato: `version` chega como inteiro OU string (int32 com
 * pattern) no OpenAPI; normalizamos sempre para `number`.
 */

const int32 = z.union([z.number().int(), z.string().regex(/^-?\d+$/)]).transform(Number);

/** Mapa de propriedades tipadas do elemento/relacionamento (string → string). */
export const propertiesSchema = z.record(z.string());
export type ArchitectureProperties = z.infer<typeof propertiesSchema>;

/** Elemento arquitetural versionado (ArchitectureElementContract). */
export const architectureElementSchema = z.object({
  id: z.string(),
  projectId: z.string().nullable(),
  kind: z.string(),
  name: z.string(),
  description: z.string(),
  properties: propertiesSchema,
  state: z.string(),
  locked: z.boolean(),
  version: int32,
});
export type ArchitectureElement = z.infer<typeof architectureElementSchema>;

export const architectureElementPageSchema = z.object({
  total: int32,
  nextCursor: z.string().nullable(),
  elements: z.array(architectureElementSchema),
});
export type ArchitectureElementPage = z.infer<typeof architectureElementPageSchema>;

/** Relacionamento entre dois elementos (ArchitectureRelationshipContract). */
export const architectureRelationshipSchema = z.object({
  id: z.string(),
  projectId: z.string().nullable(),
  sourceId: z.string(),
  targetId: z.string(),
  kind: z.string(),
  properties: propertiesSchema,
  state: z.string(),
  version: int32,
});
export type ArchitectureRelationship = z.infer<typeof architectureRelationshipSchema>;

export const architectureRelationshipListSchema = z.object({
  total: int32,
  relationships: z.array(architectureRelationshipSchema),
});
export type ArchitectureRelationshipList = z.infer<typeof architectureRelationshipListSchema>;

/** Resumo de view salva (ArchitectureViewSummaryContract). */
export const architectureViewSummarySchema = z.object({
  id: z.string(),
  projectId: z.string().nullable(),
  name: z.string(),
  notation: z.string(),
  elementCount: int32,
});
export type ArchitectureViewSummary = z.infer<typeof architectureViewSummarySchema>;

export const architectureViewListSchema = z.object({
  total: int32,
  views: z.array(architectureViewSummarySchema),
});
export type ArchitectureViewList = z.infer<typeof architectureViewListSchema>;

/** View completa: projeção resolvida do modelo (ArchitectureViewContract). */
export const architectureViewSchema = z.object({
  id: z.string(),
  projectId: z.string().nullable(),
  name: z.string(),
  description: z.string(),
  notation: z.string(),
  elements: z.array(architectureElementSchema),
  relationships: z.array(architectureRelationshipSchema),
});
export type ArchitectureView = z.infer<typeof architectureViewSchema>;

/** Dependente de um elemento (ArchitectureDependentContract). */
export const architectureDependentSchema = z.object({
  elementId: z.string(),
  name: z.string(),
  kind: z.string(),
  via: z.string(),
  direct: z.boolean(),
});
export type ArchitectureDependent = z.infer<typeof architectureDependentSchema>;

/** Entrada de histórico versionado (ArchitectureHistoryEntryContract). */
export const architectureHistoryEntrySchema = z.object({
  id: z.string(),
  entityType: z.string(),
  entityId: z.string(),
  version: int32,
  changeKind: z.string(),
  actor: z.string().nullable(),
  justification: z.string().nullable(),
  occurredAt: z.string(),
});
export type ArchitectureHistoryEntry = z.infer<typeof architectureHistoryEntrySchema>;

export const architectureHistorySchema = z.object({
  entityId: z.string(),
  history: z.array(architectureHistoryEntrySchema),
});
export type ArchitectureHistory = z.infer<typeof architectureHistorySchema>;

/* ---- inputs de mutação (request bodies do OpenAPI) ---- */

export interface CreateElementInput {
  projectId: string;
  kind: string;
  name: string;
  description: string;
  properties: ArchitectureProperties;
}

export interface CreateRelationshipInput {
  projectId: string;
  sourceId: string;
  targetId: string;
  kind: string;
  properties: ArchitectureProperties;
}

export interface CreateViewInput {
  projectId: string;
  name: string;
  description: string;
  notation: string;
  elementIds: string[];
  relationshipIds: string[];
  filterKinds: string[];
  filterTags: string[];
}

export interface LockInput {
  locked: boolean;
}

/* ================================================================== */
/* Architecture Hub — mapas, sistema 360, discovery, insights,        */
/* padrões/ADRs e baselines (ARC-02/03/06/07/08/10).                  */
/* ================================================================== */

/* ---- ARC-02 Mapa Corporativo de Sistemas ---- */

/** Entrada do catálogo de sistemas (SystemCatalogEntryContract). */
export const systemCatalogEntrySchema = z.object({
  id: z.string(),
  name: z.string(),
  description: z.string(),
  domain: z.string().nullable(),
  capabilities: z.array(z.string()),
  owner: z.string().nullable(),
  criticality: z.string(),
  lifecycleStatus: z.string().nullable(),
  heatSignals: z.array(z.string()),
  heatScore: int32,
});
export type SystemCatalogEntry = z.infer<typeof systemCatalogEntrySchema>;

export const systemCatalogSchema = z.object({
  total: int32,
  nextCursor: z.string().nullable(),
  systems: z.array(systemCatalogEntrySchema),
});
export type SystemCatalog = z.infer<typeof systemCatalogSchema>;

export const domainMapNodeSchema = z.object({
  domain: z.string(),
  systemCount: int32,
  systemIds: z.array(z.string()),
});
export type DomainMapNode = z.infer<typeof domainMapNodeSchema>;

export const domainMapSchema = z.object({
  total: int32,
  domains: z.array(domainMapNodeSchema),
});
export type DomainMap = z.infer<typeof domainMapSchema>;

export const capabilityMapNodeSchema = z.object({
  capability: z.string(),
  systemCount: int32,
  systemIds: z.array(z.string()),
});
export type CapabilityMapNode = z.infer<typeof capabilityMapNodeSchema>;

export const capabilityMapSchema = z.object({
  total: int32,
  capabilities: z.array(capabilityMapNodeSchema),
});
export type CapabilityMap = z.infer<typeof capabilityMapSchema>;

export const integrationEdgeSchema = z.object({
  sourceId: z.string(),
  sourceName: z.string(),
  targetId: z.string(),
  targetName: z.string(),
  kind: z.string(),
});
export type IntegrationEdge = z.infer<typeof integrationEdgeSchema>;

export const integrationGraphSchema = z.object({
  systemCount: int32,
  edgeCount: int32,
  edges: z.array(integrationEdgeSchema),
});
export type IntegrationGraph = z.infer<typeof integrationGraphSchema>;

export const heatSignalSchema = z.object({
  code: z.string(),
  detail: z.string(),
});
export type HeatSignal = z.infer<typeof heatSignalSchema>;

export const systemHeatmapEntrySchema = z.object({
  id: z.string(),
  name: z.string(),
  domain: z.string().nullable(),
  score: int32,
  signals: z.array(heatSignalSchema),
});
export type SystemHeatmapEntry = z.infer<typeof systemHeatmapEntrySchema>;

export const systemHeatmapSchema = z.object({
  total: int32,
  signalTotals: z.record(int32),
  systems: z.array(systemHeatmapEntrySchema),
});
export type SystemHeatmap = z.infer<typeof systemHeatmapSchema>;

/* ---- ARC-03 Sistema 360 ---- */

export const archDocumentRefSchema = z.object({
  id: z.string(),
  title: z.string(),
  kind: z.string(),
  state: z.string(),
});
export type ArchDocumentRef = z.infer<typeof archDocumentRefSchema>;

export const system360Schema = z.object({
  systemId: z.string(),
  business: z.object({
    name: z.string(),
    description: z.string(),
    domain: z.string().nullable(),
    capabilities: z.array(z.string()),
    criticality: z.string(),
    owner: z.string().nullable(),
  }),
  technology: z.object({
    techStack: z.array(z.string()),
    lifecycleStatus: z.string().nullable(),
    containers: z.array(architectureElementSchema),
  }),
  integrations: z.object({
    outgoing: z.array(integrationEdgeSchema),
    incoming: z.array(integrationEdgeSchema),
  }),
  operation: z.object({
    sla: z.string().nullable(),
    incidentCount: int32,
    lastIncidentAt: z.string().nullable(),
    backupPolicy: z.string().nullable(),
    drPolicy: z.string().nullable(),
    costMonthlyUsd: z.union([z.number(), z.string().regex(/^-?\d+(\.\d+)?$/)]).transform(Number).nullable(),
  }),
  data: z.object({
    pii: z.boolean(),
    sensitive: z.boolean(),
    retention: z.string().nullable(),
    dataClasses: z.array(z.string()),
  }),
  governance: z.object({
    documents: z.array(archDocumentRefSchema),
    adrCount: int32,
    risks: z.array(z.string()),
    lastReviewAt: z.string().nullable(),
    reviewConfidence: z.string().nullable(),
    busFactor: int32.nullable(),
  }),
});
export type System360 = z.infer<typeof system360Schema>;

/* ---- ARC-06 Discovery ---- */

export const discoverySchema = z.object({
  id: z.string(),
  projectId: z.string().nullable(),
  systemId: z.string().nullable(),
  subjectName: z.string(),
  sourceKind: z.string(),
  field: z.string(),
  value: z.string(),
  confidence: z.string(),
  evidence: z.string(),
  pendingQuestions: z.array(z.string()),
  status: z.string(),
  createdAt: z.string(),
  updatedAt: z.string(),
});
export type Discovery = z.infer<typeof discoverySchema>;

export const discoveryListSchema = z.object({
  total: int32,
  nextCursor: z.string().nullable(),
  discoveries: z.array(discoverySchema),
});
export type DiscoveryList = z.infer<typeof discoveryListSchema>;

export const discoverySystemSummarySchema = z.object({
  systemId: z.string().nullable(),
  subjectName: z.string(),
  discoveryCount: int32,
  openCount: int32,
  confirmedCount: int32,
  overallConfidence: z.string(),
  bySource: z.record(int32),
  pendingQuestions: z.array(z.string()),
});
export type DiscoverySystemSummary = z.infer<typeof discoverySystemSummarySchema>;

export const discoverySummaryListSchema = z.object({
  total: int32,
  subjects: z.array(discoverySystemSummarySchema),
});
export type DiscoverySummaryList = z.infer<typeof discoverySummaryListSchema>;

/* ---- ARC-07 Insights & Racionalização ---- */

export const rationalizationInsightSchema = z.object({
  systemId: z.string(),
  systemName: z.string(),
  category: z.string(),
  classification: z.string(),
  rationale: z.string(),
  evidence: z.string(),
  affectedDependentCount: int32,
  relatedSystemIds: z.array(z.string()),
});
export type RationalizationInsight = z.infer<typeof rationalizationInsightSchema>;

export const rationalizationReportSchema = z.object({
  systemCount: int32,
  insightCount: int32,
  byClassification: z.record(int32),
  insights: z.array(rationalizationInsightSchema),
});
export type RationalizationReport = z.infer<typeof rationalizationReportSchema>;

/* ---- ARC-08 Padrões & Decisões (ADRs) ---- */

export const architecturePatternSchema = z.object({
  id: z.string(),
  projectId: z.string().nullable(),
  kind: z.string(),
  title: z.string(),
  status: z.string(),
  context: z.string(),
  body: z.string(),
  problem: z.string().nullable(),
  consequences: z.string(),
  tags: z.array(z.string()),
  supersedesId: z.string().nullable(),
  documentId: z.string().nullable(),
  createdAt: z.string(),
  updatedAt: z.string(),
});
export type ArchitecturePattern = z.infer<typeof architecturePatternSchema>;

export const architecturePatternListSchema = z.object({
  total: int32,
  nextCursor: z.string().nullable(),
  items: z.array(architecturePatternSchema),
});
export type ArchitecturePatternList = z.infer<typeof architecturePatternListSchema>;

/* ---- ARC-10 Baselines & Conformidade ---- */

export const architectureBaselineSchema = z.object({
  id: z.string(),
  projectId: z.string(),
  status: z.string(),
  title: z.string(),
  proposalId: z.string().nullable(),
  baselineElementCount: int32,
  hasAsBuilt: z.boolean(),
  createdAt: z.string(),
  updatedAt: z.string(),
});
export type ArchitectureBaseline = z.infer<typeof architectureBaselineSchema>;

export const architectureBaselineListSchema = z.object({
  total: int32,
  baselines: z.array(architectureBaselineSchema),
});
export type ArchitectureBaselineList = z.infer<typeof architectureBaselineListSchema>;

export const baselineDriftEntrySchema = z.object({
  elementId: z.string(),
  name: z.string(),
  kind: z.string(),
  drift: z.string(),
});
export type BaselineDriftEntry = z.infer<typeof baselineDriftEntrySchema>;

export const baselineComparisonSchema = z.object({
  baselineId: z.string(),
  matched: int32,
  missing: int32,
  unplanned: int32,
  edgeMatched: int32,
  edgeMissing: int32,
  edgeUnplanned: int32,
  conformancePercent: z.union([z.number(), z.string().regex(/^-?\d+(\.\d+)?$/)]).transform(Number),
  drifts: z.array(baselineDriftEntrySchema),
});
export type BaselineComparison = z.infer<typeof baselineComparisonSchema>;

export const portfolioReuseCandidateSchema = z.object({
  systemId: z.string(),
  name: z.string(),
  domain: z.string().nullable(),
  matchedCapabilities: z.array(z.string()),
  duplicateFlag: z.boolean(),
});
export type PortfolioReuseCandidate = z.infer<typeof portfolioReuseCandidateSchema>;

export const portfolioReuseSchema = z.object({
  capability: z.string().nullable(),
  domain: z.string().nullable(),
  candidateCount: int32,
  candidates: z.array(portfolioReuseCandidateSchema),
});
export type PortfolioReuse = z.infer<typeof portfolioReuseSchema>;
