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
