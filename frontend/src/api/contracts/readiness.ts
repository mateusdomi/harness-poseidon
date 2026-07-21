import { z } from 'zod';

/**
 * Prontidão canônica do projeto (read model do backend, ADR-017).
 *
 * Espelha `ProjectReadinessSnapshot` do OpenAPI. Esta é a FONTE DA VERDADE da
 * jornada do golden path a partir do momento em que o projeto existe — o
 * frontend não recalcula o que o backend já decide.
 *
 * Códigos (`messageCode`, `Blocker.code`, `NextAction.code`) são estáveis e
 * localizáveis na UI; nunca texto de domínio pronto para exibir.
 */

/** Estado de configuração fechado de uma dependência do golden path. */
export const configurationStateSchema = z.enum([
  'Unconfigured',
  'Simulated',
  'Configured',
  'Ready',
  'Degraded',
  'Unavailable',
]);
export type ConfigurationState = z.infer<typeof configurationStateSchema>;

/** Etapas na ordem canônica publicada pelo backend. */
export const readinessStepSchema = z.enum([
  'ProfileReady',
  'OrganizationReady',
  'ProjectReady',
  'ProviderAccountReady',
  'ModelReady',
  'WorkflowReady',
  'ChiefDefinitionReady',
  'AgentPoolReady',
  'ExecutionReady',
]);
export type ReadinessStep = z.infer<typeof readinessStepSchema>;
export const READINESS_STEPS = readinessStepSchema.options;

/** Bloqueador tipado: código estável + ids relacionados (nunca texto livre). */
export const readinessBlockerSchema = z.object({
  code: z.string(),
  relatedIds: z.array(z.string()),
});
export type ReadinessBlocker = z.infer<typeof readinessBlockerSchema>;

/** Próxima ação recomendada: código estável, rota e recurso opcional. */
export const readinessNextActionSchema = z.object({
  code: z.string(),
  route: z.string(),
  resourceId: z.string().nullable(),
});
export type ReadinessNextAction = z.infer<typeof readinessNextActionSchema>;

export const readinessStepContractSchema = z.object({
  step: readinessStepSchema,
  state: configurationStateSchema,
  /** `real` | `simulated` | `unconfigured` — como a etapa executaria hoje. */
  executionMode: z.string(),
  capability: z.string(),
  messageCode: z.string(),
  relatedIds: z.array(z.string()),
  blockers: z.array(readinessBlockerSchema),
  nextAction: readinessNextActionSchema.nullable(),
});
export type ReadinessStepContract = z.infer<typeof readinessStepContractSchema>;

export const projectReadinessSnapshotSchema = z.object({
  projectId: z.string().nullable(),
  overallState: configurationStateSchema,
  steps: z.array(readinessStepContractSchema),
  nextActions: z.array(readinessNextActionSchema),
});
export type ProjectReadinessSnapshot = z.infer<typeof projectReadinessSnapshotSchema>;
