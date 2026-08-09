import { z } from 'zod';

import { isoDateTimeSchema, ulidSchema } from './primitives';

/**
 * Identidade de execução da fleet do Chefe (roster REDIGIDO de `agent-accounts`).
 *
 * Contrato espelha `GET /api/v1/agent-accounts`: apenas dados seguros de exibição —
 * alias, provider, executor, papéis lógicos, limites e estado. NÃO existe campo de
 * credencial ou token: a redação é estrutural no backend e aqui também. Estas são as
 * IDENTIDADES DE EXECUÇÃO (quem roda o trabalho), distintas das personas/definições de
 * agente (o que o agente é).
 */
export const agentAccountRosterSchema = z.object({
  /** Apelido canônico (ex.: `chief-claude-primary`). Nunca e-mail, nunca segredo. */
  alias: z.string(),
  /** Provedor lógico (ex.: `anthropic`, `openai`, `zhipu`, `moonshot`, `antigravity`). */
  providerKind: z.string(),
  /** Executor concreto (ex.: `claude-code`, `codex`, `glm`, `kimi-code`, `antigravity`). */
  executorId: z.string(),
  /** Papéis lógicos permitidos (o escopo pertence ao papel, nunca ao provider). */
  roles: z.array(z.string()),
  /** Execuções simultâneas permitidas para esta identidade (>= 1). */
  concurrencyLimit: z.number().int().positive(),
  /** Prioridade de seleção (maior = preferida dentro do papel). */
  priority: z.number().int(),
  /** Habilitada na configuração local do operador. */
  enabled: z.boolean(),
  /**
   * Estado inicial da conta. Uma conta nunca nasce disponível: instalação e
   * autenticação são comprovadas por probe/login, jamais presumidas.
   */
  state: z.enum([
    'working',
    'idle',
    'out-of-quota',
    'cooldown',
    'authentication-required',
    'offline',
    'degraded',
    'disabled',
  ]),
  /** Saúde segura para exibição; não contém detalhe de credencial. */
  health: z.enum(['healthy', 'attention', 'unhealthy']).optional(),
  /** Quando cota/cooldown retorna; nulo quando o provedor não informa. */
  returnsAt: isoDateTimeSchema.nullable().optional(),
  /** Código operacional redigido, nunca mensagem com segredo. */
  reasonCode: z.string().nullable().optional(),
});
export type AgentAccountRoster = z.infer<typeof agentAccountRosterSchema>;

export const v3AccountAuthInstructionSchema = z.object({
  alias: z.string(),
  providerKind: z.string(),
  executorId: z.string(),
  configHomePath: z.string(),
  configHomeEnvironmentVariable: z.string().nullable().optional(),
  command: z.string(),
  arguments: z.array(z.string()),
  shellCommand: z.string(),
  instruction: z.string(),
  accountsFilePath: z.string().nullable().optional(),
});
export type V3AccountAuthInstruction = z.infer<typeof v3AccountAuthInstructionSchema>;

export const v3ChiefAssignmentSchema = z.object({
  primaryAlias: z.string().nullable(),
  providerKind: z.string().nullable().optional(),
  executorId: z.string().nullable().optional(),
  state: z.string().nullable().optional(),
  accountsFilePath: z.string(),
});
export type V3ChiefAssignment = z.infer<typeof v3ChiefAssignmentSchema>;

/** Tipos de canal externo suportados pelo gateway (conjunto fechado). */
export const channelKindSchema = z.enum(['terminal', 'telegram', 'teams', 'whatsapp', 'email']);
export type ChannelKind = z.infer<typeof channelKindSchema>;

/**
 * Vínculo de um canal externo (ex.: Telegram) a um projeto/conversa.
 * Espelha `ChannelLinkContract` de `GET /api/v1/channels/links`.
 */
export const channelLinkSchema = z.object({
  id: ulidSchema,
  kind: channelKindSchema,
  /** Identidade externa (ex.: chat id do Telegram). Nunca um segredo de bot. */
  externalIdentity: z.string(),
  projectId: ulidSchema,
  conversationId: ulidSchema,
  linkedAt: isoDateTimeSchema,
});
export type ChannelLink = z.infer<typeof channelLinkSchema>;

/**
 * Entrada para vincular um canal externo a um projeto.
 * Espelha `CreateChannelLinkRequest` de `POST /api/v1/channels/links`.
 */
export const createChannelLinkInputSchema = z.object({
  kind: channelKindSchema,
  /** Identidade externa (ex.: chat id numérico do Telegram). Nunca um token de bot. */
  externalIdentity: z.string().trim().min(1).max(200),
  projectId: ulidSchema,
  /** Conversa ativa existente do projeto; ausente cria uma conversa dedicada ao canal. */
  conversationId: ulidSchema.optional(),
});
export type CreateChannelLinkInput = z.infer<typeof createChannelLinkInputSchema>;

/** Mensagem trocada por um canal externo (histórico do link). */
export const channelMessageSchema = z.object({
  id: ulidSchema,
  authorRole: z.string(),
  content: z.string(),
  createdAt: isoDateTimeSchema,
});
export type ChannelMessage = z.infer<typeof channelMessageSchema>;

/** Página de mensagens de um canal (cursor opaco `afterMessageId`). */
export const channelMessagePageSchema = z.object({
  items: z.array(channelMessageSchema),
  nextCursor: z.string().nullable(),
});
export type ChannelMessagePage = z.infer<typeof channelMessagePageSchema>;
