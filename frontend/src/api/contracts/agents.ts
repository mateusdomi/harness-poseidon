import { z } from 'zod';

import {
  actorCriticSchema,
  agentDefinitionStateSchema,
  agentRoleSchema,
  agentStateSchema,
  componentStateSchema,
  effortLevelSchema,
  mcpTransportSchema,
  riskLevelSchema,
  toolKindSchema,
} from './enums';
import { isoDateTimeSchema, ulidSchema } from './primitives';

/** Entrada do histórico de revisões de uma definição de agente (FR-5). */
export const agentDefinitionRevisionSchema = z.object({
  version: z.number().int().positive(),
  changedAt: isoDateTimeSchema,
  /** Campos alterados nesta revisão. */
  changedFields: z.array(z.string()),
  /** Resumo legível da mudança. */
  summary: z.string(),
});
export type AgentDefinitionRevision = z.infer<typeof agentDefinitionRevisionSchema>;

/**
 * Definição (tipo) de agente: chefe ou especialista.
 * Campos FR-5 são ADITIVOS e opcionais (o backend pode ainda não enviá-los):
 * persona/missão/responsabilidades/instruções/restrições/boas práticas,
 * stacks, esforço padrão, conta preferencial, fallbacks, time, actor/critic,
 * risco, estado do ciclo de vida e versionamento (`version` + `history`).
 */
export const agentDefinitionSchema = z.object({
  id: ulidSchema,
  key: z.string(),
  name: z.string(),
  role: agentRoleSchema,
  specialty: z.string().nullable(),
  description: z.string(),
  defaultModelId: ulidSchema.nullable(),
  skillIds: z.array(ulidSchema),
  toolIds: z.array(ulidSchema),
  /* ---- FR-5 (aditivos) ---- */
  state: agentDefinitionStateSchema.optional(),
  persona: z.string().nullable().optional(),
  mission: z.string().nullable().optional(),
  responsibilities: z.string().nullable().optional(),
  instructions: z.string().nullable().optional(),
  restrictions: z.string().nullable().optional(),
  bestPractices: z.string().nullable().optional(),
  stacks: z.array(z.string()).optional(),
  defaultEffort: effortLevelSchema.nullable().optional(),
  /** Conta de provider preferencial (o provider se resolve pela conta). */
  preferredAccountId: ulidSchema.nullable().optional(),
  /** Modelos alternativos, em ordem, quando o padrão não está disponível. */
  fallbackModelIds: z.array(ulidSchema).optional(),
  /** Time ao qual a definição pertence (agrupa o organograma). */
  team: z.string().nullable().optional(),
  actorCritic: actorCriticSchema.nullable().optional(),
  risk: riskLevelSchema.nullable().optional(),
  /** Versão da definição — incrementa a cada edição (como os demais recursos). */
  version: z.number().int().positive().optional(),
  /** Histórico simples de revisões (mais recente por último). */
  history: z.array(agentDefinitionRevisionSchema).optional(),
  /* Campos do contrato real V3 (2026-07-19). Permanecem junto aos campos
     aditivos do refinamento para compatibilidade progressiva. */
  operatingPrinciples: z.array(z.string()).optional(),
  deliverables: z.array(z.string()).optional(),
  qualityCriteria: z.array(z.string()).optional(),
  communicationStyle: z.string().nullable().optional(),
  limitations: z.array(z.string()).optional(),
  enabled: z.boolean().optional(),
  archivedAt: isoDateTimeSchema.nullable().optional(),
  /* PROCEDÊNCIA (2026-07-27). A chefe passou a criar especialistas sozinha quando há lacuna real,
     então a auditoria humana precisa ver, na tela, QUEM criou, POR QUÊ, em que projeto e em que
     estágio de confiança o perfil está. Sem isso a supervisão prometida não tem o que supervisionar.
     Aditivos e opcionais: um backend anterior a esta rodada continua válido. */
  origin: z.enum(['human', 'chief', 'system']).optional(),
  lifecycleState: z
    .enum(['project_scoped', 'active', 'reusable', 'global', 'observation', 'quarantined', 'disabled'])
    .optional(),
  scopeProjectId: ulidSchema.nullable().optional(),
  creationReason: z.string().nullable().optional(),
});
export type AgentDefinition = z.infer<typeof agentDefinitionSchema>;

/** Métricas acumuladas de um agente. */
export const agentMetricsSchema = z.object({
  tasksCompleted: z.number().int().nonnegative(),
  tokensInput: z.number().int().nonnegative(),
  tokensOutput: z.number().int().nonnegative(),
  costUsd: z.number().nonnegative(),
  uptimeMs: z.number().int().nonnegative(),
});
export type AgentMetrics = z.infer<typeof agentMetricsSchema>;

/**
 * Concessão de orquestração (diagnóstico avançado): quem detém o lease
 * é o orquestrador vigente; `fencingToken` cresce a cada passagem de
 * bastão e invalida escritores antigos.
 */
export const agentLeaseSchema = z.object({
  fencingToken: z.number().int().nonnegative(),
  expiresAt: isoDateTimeSchema,
});
export type AgentLease = z.infer<typeof agentLeaseSchema>;

/** Instância de agente em execução (o que aparece na UI de agentes). */
export const agentSchema = z.object({
  id: ulidSchema,
  definitionId: ulidSchema,
  /** Projeto ao qual a instância está alocada (nulo = pool global). */
  projectId: ulidSchema.nullable(),
  name: z.string(),
  state: agentStateSchema,
  currentTaskId: ulidSchema.nullable(),
  /** Override de modelo (passagem de bastão); nulo = `defaultModelId` da definição. */
  modelId: ulidSchema.nullable(),
  /** Lease/fencing do orquestrador (só chefes; diagnóstico avançado). */
  lease: agentLeaseSchema.nullable(),
  metrics: agentMetricsSchema,
  lastHeartbeatAt: isoDateTimeSchema.nullable(),
  /** Seleção operacional persistida pelo backend V3. */
  accountId: ulidSchema.nullable().optional(),
  effort: effortLevelSchema.nullable().optional(),
  providerEffortValue: z.string().nullable().optional(),
  fallbackModelIds: z.array(ulidSchema).optional(),
  selectionReason: z.string().nullable().optional(),
  selectionUpdatedAt: isoDateTimeSchema.nullable().optional(),
});
export type Agent = z.infer<typeof agentSchema>;

export const skillSchema = z.object({
  id: ulidSchema,
  key: z.string(),
  name: z.string(),
  description: z.string(),
  version: z.string(),
  state: componentStateSchema,
});
export type Skill = z.infer<typeof skillSchema>;

export const toolSchema = z.object({
  id: ulidSchema,
  key: z.string(),
  name: z.string(),
  description: z.string(),
  kind: toolKindSchema,
  state: componentStateSchema,
});
export type Tool = z.infer<typeof toolSchema>;

export const pluginSchema = z.object({
  id: ulidSchema,
  key: z.string(),
  name: z.string(),
  version: z.string(),
  description: z.string(),
  state: componentStateSchema,
  providesToolIds: z.array(ulidSchema),
});
export type Plugin = z.infer<typeof pluginSchema>;

export const mcpServerSchema = z.object({
  id: ulidSchema,
  name: z.string(),
  transport: mcpTransportSchema,
  /** Comando (stdio) ou URL (http). */
  endpoint: z.string(),
  state: componentStateSchema,
  toolCount: z.number().int().nonnegative(),
});
export type McpServer = z.infer<typeof mcpServerSchema>;
