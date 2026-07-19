import { z } from 'zod';

/**
 * Enums centrais do domínio Poseidon.
 * NENHUM status/estado pode ser string solta fora daqui — UI e backend
 * compartilham estes valores (labels pt-BR ficam no i18n da UI).
 */

/** Colunas do quadro de tarefas. */
export const taskStateSchema = z.enum([
  'backlog',
  'ready',
  'development',
  'review',
  'corrections',
  'testsGates',
  'blocked',
  'done',
]);
export type TaskState = z.infer<typeof taskStateSchema>;
export const TASK_STATES = taskStateSchema.options;

export const prioritySchema = z.enum(['low', 'medium', 'high', 'critical']);
export type Priority = z.infer<typeof prioritySchema>;
export const PRIORITIES = prioritySchema.options;

/** Ciclo de vida de um projeto. */
export const projectStateSchema = z.enum(['active', 'paused', 'archived']);
export type ProjectState = z.infer<typeof projectStateSchema>;
export const PROJECT_STATES = projectStateSchema.options;

/** Provedor de repositório do projeto (URL remota ou caminho local). */
export const repositoryProviderSchema = z.enum(['github', 'gitlab', 'bitbucket', 'local', 'other']);
export type RepositoryProvider = z.infer<typeof repositoryProviderSchema>;
export const REPOSITORY_PROVIDERS = repositoryProviderSchema.options;

/** Estado de uma tentativa (attempt) de execução de tarefa. */
export const attemptStateSchema = z.enum(['queued', 'running', 'completed', 'failed', 'cancelled']);
export type AttemptState = z.infer<typeof attemptStateSchema>;

/** Máquina de estados de documentos. */
export const documentStateSchema = z.enum([
  'planned',
  'inElaboration',
  'inReview',
  'awaitingApproval',
  'approved',
  'outdated',
  'superseded',
  'notApplicable',
]);
export type DocumentState = z.infer<typeof documentStateSchema>;

/** Estados de um agente (instância). */
export const agentStateSchema = z.enum(['working', 'idle', 'waiting', 'error', 'outOfQuota']);
export type AgentState = z.infer<typeof agentStateSchema>;
export const AGENT_STATES = agentStateSchema.options;

/** Papel do agente: chefe coordena o projeto e delega a especialistas. */
export const agentRoleSchema = z.enum(['chief', 'specialist']);
export type AgentRole = z.infer<typeof agentRoleSchema>;

/** Modos de operação do workflow (troca exige confirmação + aceite de risco). */
export const operationModeSchema = z.enum(['manual', 'semiautonomous', 'autonomous']);
export type OperationMode = z.infer<typeof operationModeSchema>;

export const gateStateSchema = z.enum(['pending', 'approved', 'rejected', 'waived']);
export type GateState = z.infer<typeof gateStateSchema>;

export const approvalStateSchema = z.enum(['pending', 'approved', 'rejected', 'cancelled']);
export type ApprovalState = z.infer<typeof approvalStateSchema>;

export const phaseStateSchema = z.enum(['pending', 'active', 'completed', 'skipped', 'failed']);
export type PhaseState = z.infer<typeof phaseStateSchema>;

export const workflowRunStateSchema = z.enum([
  'running',
  'paused',
  'completed',
  'failed',
  'cancelled',
]);
export type WorkflowRunState = z.infer<typeof workflowRunStateSchema>;

/**
 * Ciclo de vida de conteúdo de workflow (template/versão): rascunho editável,
 * publicado imutável, arquivado (tombstone — nunca excluído fisicamente).
 */
export const workflowContentStateSchema = z.enum(['draft', 'published', 'archived']);
export type WorkflowContentState = z.infer<typeof workflowContentStateSchema>;
export const WORKFLOW_CONTENT_STATES = workflowContentStateSchema.options;

/** Solicitações são criadas por humanos e triadas pelo chefe (imutáveis). */
export const solicitationKindSchema = z.enum(['request', 'intervention']);
export type SolicitationKind = z.infer<typeof solicitationKindSchema>;

export const solicitationStateSchema = z.enum([
  'open',
  'inAnalysis',
  'converted',
  'answered',
  'closed',
]);
export type SolicitationState = z.infer<typeof solicitationStateSchema>;

/** Demandas são criadas pelo chefe a partir de solicitações/conversa. */
export const demandStateSchema = z.enum(['open', 'inProgress', 'completed', 'cancelled']);
export type DemandState = z.infer<typeof demandStateSchema>;

export const messageAuthorRoleSchema = z.enum(['user', 'chief', 'agent', 'system']);
export type MessageAuthorRole = z.infer<typeof messageAuthorRoleSchema>;

export const conversationStateSchema = z.enum(['active', 'archived']);
export type ConversationState = z.infer<typeof conversationStateSchema>;

/** Estado do turno do chefe (orquestração visível no chat). */
export const chiefTurnStateSchema = z.enum([
  'thinking',
  'delegating',
  'waitingApproval',
  'streaming',
  'idle',
]);
export type ChiefTurnState = z.infer<typeof chiefTurnStateSchema>;

export const notificationSeveritySchema = z.enum(['info', 'warning', 'error', 'critical']);
export type NotificationSeverity = z.infer<typeof notificationSeveritySchema>;

export const notificationCategorySchema = z.enum([
  'system',
  'task',
  'approval',
  'quota',
  'license',
  'chat',
  'workflow',
]);
export type NotificationCategory = z.infer<typeof notificationCategorySchema>;

export const notificationStatusSchema = z.enum(['unread', 'read', 'muted']);
export type NotificationStatus = z.infer<typeof notificationStatusSchema>;

/** Estado da licença do dispositivo. */
export const licenseStateSchema = z.enum([
  'active',
  'gracePeriod',
  'expired',
  'offline',
  'unlicensed',
]);
export type LicenseState = z.infer<typeof licenseStateSchema>;

export const prototypeStateSchema = z.enum([
  'draft',
  'generating',
  'ready',
  'published',
  'archived',
]);
export type PrototypeState = z.infer<typeof prototypeStateSchema>;

export const visualReferenceSourceSchema = z.enum(['upload', 'url', 'generated']);
export type VisualReferenceSource = z.infer<typeof visualReferenceSourceSchema>;

/**
 * Cenário de prototipação do projeto (seleção por projeto):
 * protótipo externo, apenas diretrizes, geração autônoma ou não aplicável
 * (dispensa formal — exige waiver com motivo).
 */
export const prototypingModeSchema = z.enum([
  'externalPrototype',
  'guidelinesOnly',
  'autonomousGeneration',
  'notApplicable',
]);
export type PrototypingMode = z.infer<typeof prototypingModeSchema>;
export const PROTOTYPING_MODES = prototypingModeSchema.options;

/** Estado operacional de ferramentas, skills, plugins e servidores MCP. */
export const componentStateSchema = z.enum(['enabled', 'disabled', 'error']);
export type ComponentState = z.infer<typeof componentStateSchema>;

export const toolKindSchema = z.enum(['builtin', 'mcp', 'plugin']);
export type ToolKind = z.infer<typeof toolKindSchema>;

export const mcpTransportSchema = z.enum(['stdio', 'http']);
export type McpTransport = z.infer<typeof mcpTransportSchema>;

export const providerKindSchema = z.enum([
  'openai',
  'anthropic',
  'azureOpenai',
  'google',
  'ollama',
  'custom',
]);
export type ProviderKind = z.infer<typeof providerKindSchema>;

export const accountStateSchema = z.enum(['active', 'disabled', 'quotaExceeded']);
export type AccountState = z.infer<typeof accountStateSchema>;

export const modelCapabilitySchema = z.enum(['chat', 'code', 'vision', 'embeddings']);
export type ModelCapability = z.infer<typeof modelCapabilitySchema>;

export const budgetPeriodSchema = z.enum(['daily', 'weekly', 'monthly']);
export type BudgetPeriod = z.infer<typeof budgetPeriodSchema>;

export const budgetScopeSchema = z.enum(['global', 'project', 'account']);
export type BudgetScope = z.infer<typeof budgetScopeSchema>;

export const runTargetKindSchema = z.enum(['http', 'tcp', 'process']);
export type RunTargetKind = z.infer<typeof runTargetKindSchema>;

export const runTargetStateSchema = z.enum(['running', 'stopped', 'unknown']);
export type RunTargetState = z.infer<typeof runTargetStateSchema>;

export const auditActorKindSchema = z.enum(['user', 'chief', 'agent', 'system']);
export type AuditActorKind = z.infer<typeof auditActorKindSchema>;

/** Quem criou uma instrução/versão (correção cria nova versão, nunca edita). */
export const instructionAuthorKindSchema = z.enum(['chief', 'user']);
export type InstructionAuthorKind = z.infer<typeof instructionAuthorKindSchema>;

export const documentKindSchema = z.enum(['prd', 'spec', 'design', 'runbook', 'note', 'report']);
export type DocumentKind = z.infer<typeof documentKindSchema>;

export const themeSchema = z.enum(['dark', 'light', 'system']);
export type ThemePreference = z.infer<typeof themeSchema>;
