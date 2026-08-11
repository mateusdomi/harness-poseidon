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

export const v3ArtifactReferenceSchema = z.object({
  artifactId: z.string(),
  name: z.string(),
  contentType: z.string(),
  role: z.string(),
  source: z.string(),
  pathReference: z.string(),
  sha256: z.string(),
  state: z.string(),
});
export type V3ArtifactReference = z.infer<typeof v3ArtifactReferenceSchema>;

export const v3DocumentReferenceSchema = z.object({
  documentId: z.string(),
  title: z.string(),
  kind: z.string(),
  state: z.string(),
  currentVersion: z.number().int(),
  classifications: z.array(z.string()),
});
export type V3DocumentReference = z.infer<typeof v3DocumentReferenceSchema>;

export const v3EffectiveStackSchema = z.object({
  frontend: z.string(),
  backend: z.string(),
  database: z.string(),
  architecture: z.string(),
  testing: z.string(),
  provenance: z.array(z.string()),
});
export type V3EffectiveStack = z.infer<typeof v3EffectiveStackSchema>;

export const v3SourceCoverageSchema = z.object({
  artifactId: z.string(),
  fileName: z.string(),
  role: z.string(),
  totalSections: z.number().int(),
  consumedSections: z.number().int(),
  coveragePercent: z.number().int(),
  complete: z.boolean(),
  evidenceProvider: z.string(),
  characterCount: z.number().int(),
});
export type V3SourceCoverage = z.infer<typeof v3SourceCoverageSchema>;

export const v3RequirementSourceFactsSchema = z.object({
  deadline: isoDateTimeSchema.nullable(),
  deadlineProvenance: z.string().nullable(),
  authentication: z.string().nullable(),
  authenticationProvenance: z.string().nullable(),
  productNotification: z.string().nullable(),
  productNotificationProvenance: z.string().nullable(),
  itrcRules: z.string().nullable(),
  itrcRulesProvenance: z.string().nullable(),
  acceptanceCriteriaCount: z.number().int(),
});
export type V3RequirementSourceFacts = z.infer<typeof v3RequirementSourceFactsSchema>;

export const v3OpenQuestionSchema = z.object({
  questionId: z.string(),
  question: z.string(),
  reason: z.string(),
});
export type V3OpenQuestion = z.infer<typeof v3OpenQuestionSchema>;

export const v3ReadinessItemSchema = z.object({
  category: z.string(),
  status: z.string(),
  evidenceProvider: z.string(),
});
export type V3ReadinessItem = z.infer<typeof v3ReadinessItemSchema>;

export const v3ExecutionCapacitySchema = z.object({
  asOf: isoDateTimeSchema,
  chiefSlots: z.number().int(),
  writeExecutorSlots: z.number().int(),
  reviewValidationSlots: z.number().int(),
  effectiveExecutionSlots: z.number().int(),
  accounts: z.array(z.unknown()),
});
export type V3ExecutionCapacity = z.infer<typeof v3ExecutionCapacitySchema>;

export const v3ProjectContextSchema = z.object({
  projectId: z.string(),
  projectName: z.string(),
  originalIntent: z.string().nullable(),
  artifacts: z.array(v3ArtifactReferenceSchema),
  documents: z.array(v3DocumentReferenceSchema),
  prototypes: z.array(z.unknown()),
  state: z.unknown().nullable(),
  productGoal: z.string().nullable(),
  projectSummary: z.string().nullable(),
  requirements: z.array(z.string()),
  acceptanceCriteria: z.array(z.string()),
  decisions: z.array(z.string()),
  assumptions: z.array(z.string()),
  primaryRequirementsCoverage: z.array(v3SourceCoverageSchema),
  sourceFacts: v3RequirementSourceFactsSchema,
  openQuestions: z.array(v3OpenQuestionSchema),
  effectiveStack: v3EffectiveStackSchema,
  deadline: isoDateTimeSchema.nullable(),
  repository: z.string().nullable(),
  runtimeEnvironment: z.string(),
  notificationChannel: z.string(),
  executionCapacity: v3ExecutionCapacitySchema,
  readiness: z.array(v3ReadinessItemSchema),
  currentLifecycleState: z.string(),
});
export type V3ProjectContext = z.infer<typeof v3ProjectContextSchema>;

export const v3HumanAcceptanceSchema = z.object({
  projectId: z.string(),
  lifecycleState: z.string(),
  status: z.string(),
  updatedAt: isoDateTimeSchema,
  message: z.string(),
});
export type V3HumanAcceptance = z.infer<typeof v3HumanAcceptanceSchema>;

export const v3KnowledgeReferenceSchema = z.object({
  path: z.string(),
  reason: z.string(),
});
export type V3KnowledgeReference = z.infer<typeof v3KnowledgeReferenceSchema>;

export const v3RecommendedExecutorSchema = z.object({
  accountAlias: z.string().nullable(),
  status: z.string(),
  reason: z.string(),
});
export type V3RecommendedExecutor = z.infer<typeof v3RecommendedExecutorSchema>;

export const v3BuildMissionSchema = z.object({
  missionId: z.string(),
  projectId: z.string(),
  missionType: z.string(),
  version: z.number().int(),
  createdAt: isoDateTimeSchema,
  createdBy: z.string(),
  targetExecutorCapability: z.string(),
  missionText: z.string(),
  approximateCharacters: z.number().int(),
  artifactReferences: z.array(v3ArtifactReferenceSchema),
  knowledgeReferences: z.array(v3KnowledgeReferenceSchema),
  effectiveStack: v3EffectiveStackSchema,
  deadline: isoDateTimeSchema.nullable(),
  repository: z.string().nullable(),
  status: z.string(),
  recommendedExecutor: v3RecommendedExecutorSchema,
  primaryRequirementsCoverage: z.array(v3SourceCoverageSchema),
});
export type V3BuildMission = z.infer<typeof v3BuildMissionSchema>;

export const v3MissionPageSchema = z.object({
  items: z.array(v3BuildMissionSchema),
});
export type V3MissionPage = z.infer<typeof v3MissionPageSchema>;

export interface V3UnderstandAnalyzeInput {
  originalIntent?: string;
  brunaSummary?: string;
  productGoal?: string;
  primaryUsers?: string[];
  coreCapabilities?: string[];
  acceptanceCriteria?: string[];
  importantConstraints?: string[];
  assumptions?: string[];
  deadline?: string | null;
  repository?: string | null;
}

export interface V3AuthorizeBuildInput {
  response: string;
  deadline?: string | null;
  repository?: string | null;
}

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
