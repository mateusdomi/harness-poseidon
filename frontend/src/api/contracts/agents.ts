import { z } from 'zod';

import {
  agentRoleSchema,
  agentStateSchema,
  componentStateSchema,
  mcpTransportSchema,
  toolKindSchema,
} from './enums';
import { isoDateTimeSchema, ulidSchema } from './primitives';

/** Definição (tipo) de agente: chefe ou especialista. */
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

/** Instância de agente em execução (o que aparece na UI de agentes). */
export const agentSchema = z.object({
  id: ulidSchema,
  definitionId: ulidSchema,
  /** Projeto ao qual a instância está alocada (nulo = pool global). */
  projectId: ulidSchema.nullable(),
  name: z.string(),
  state: agentStateSchema,
  currentTaskId: ulidSchema.nullable(),
  metrics: agentMetricsSchema,
  lastHeartbeatAt: isoDateTimeSchema.nullable(),
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
