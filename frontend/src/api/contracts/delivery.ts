import { z } from 'zod';

import {
  approvalStateSchema,
  attemptStateSchema,
  documentKindSchema,
  gateStateSchema,
  instructionAuthorKindSchema,
  operationModeSchema,
  phaseStateSchema,
  prioritySchema,
  taskStateSchema,
  workflowContentStateSchema,
  workflowRunStateSchema,
} from './enums';
import { isoDateTimeSchema, progressSchema, ulidSchema } from './primitives';

/**
 * Tarefa técnica — criada pelo chefe, NUNCA por humano.
 * `state` é a coluna do quadro; progresso em três trilhas separadas.
 */
export const taskSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  demandId: ulidSchema.nullable(),
  title: z.string(),
  state: taskStateSchema,
  priority: prioritySchema,
  assigneeAgentId: ulidSchema.nullable(),
  /** Motivo do bloqueio quando `state === 'blocked'`. */
  blockedReason: z.string().nullable(),
  /** Versão da instrução vigente (`task-instructions`). */
  instructionVersion: z.number().int().positive(),
  progress: progressSchema,
  createdAt: isoDateTimeSchema,
  updatedAt: isoDateTimeSchema,
  dueAt: isoDateTimeSchema.nullable(),
  /** Fase canônica do workflow associada à tarefa, quando informada. */
  phaseName: z.string().nullable().optional(),
  /** Tipo operacional persistido do card (tarefa, gate, feature, spike ou decisão). */
  cardType: z
    .enum([
      // tipos pré-playbook (compatibilidade)
      'feature', 'agent_task', 'human_gate', 'spike', 'decision',
      // tipos canônicos do playbook (§3)
      'historia', 'tarefa', 'bug', 'adr', 'documento',
      'revisao', 'gate', 'incidente', 'chamado',
    ])
    .optional(),
  /**
   * Arquivamento é um METAESTADO (não entra na máquina de estados):
   * a tarefa arquivada some do quadro padrão, mas mantém estado/histórico
   * e volta pelo filtro "arquivadas" ou pelo desarquivamento.
   */
  archivedAt: isoDateTimeSchema.nullable(),
});
export type Task = z.infer<typeof taskSchema>;

/**
 * Instrução de tarefa — IMUTÁVEL e versionada.
 * Correção cria nova versão (sem PUT). `version` começa em 1.
 */
export const taskInstructionSchema = z.object({
  id: ulidSchema,
  taskId: ulidSchema,
  version: z.number().int().positive(),
  body: z.string(),
  authorKind: instructionAuthorKindSchema,
  authorId: ulidSchema.nullable(),
  createdAt: isoDateTimeSchema,
});
export type TaskInstruction = z.infer<typeof taskInstructionSchema>;

/** Tentativa de execução de uma tarefa, com evidências de custo/tokens. */
export const attemptSchema = z.object({
  id: ulidSchema,
  taskId: ulidSchema,
  /** Número sequencial da tentativa dentro da tarefa (1, 2, 3...). */
  number: z.number().int().positive(),
  state: attemptStateSchema,
  agentId: ulidSchema,
  startedAt: isoDateTimeSchema,
  finishedAt: isoDateTimeSchema.nullable(),
  durationMs: z.number().int().nonnegative().nullable(),
  costUsd: z.number().nonnegative(),
  tokensInput: z.number().int().nonnegative(),
  tokensOutput: z.number().int().nonnegative(),
  /** Commits/diffs referenciados como evidência (SHAs ou refs). */
  commitRefs: z.array(z.string()),
  summary: z.string().nullable(),
  /** Motivo da falha quando `state === 'failed'`. */
  failureReason: z.string().nullable(),
});
export type Attempt = z.infer<typeof attemptSchema>;

/** Evento/linha de evidência de uma tentativa (logs, chamadas de ferramenta). */
export const attemptEventSchema = z.object({
  id: ulidSchema,
  attemptId: ulidSchema,
  kind: z.enum(['log', 'toolCall', 'note', 'diff']),
  content: z.string(),
  occurredAt: isoDateTimeSchema,
});
export type AttemptEvent = z.infer<typeof attemptEventSchema>;

export const workflowTemplateSchema = z.object({
  id: ulidSchema,
  name: z.string(),
  description: z.string(),
  currentVersionId: ulidSchema.nullable(),
  /**
   * Ciclo de vida (FR-4, aditivo): rascunho editável → publicado (imutável)
   * → arquivado (tombstone; template utilizado NUNCA é excluído fisicamente).
   */
  state: workflowContentStateSchema,
  archivedAt: isoDateTimeSchema.nullable(),
  /**
   * O recomendado canônico do PRODUTO (a esteira do playbook), publicado pelo backend —
   * a UI pré-seleciona este template sem heurística.
   */
  recommended: z.boolean(),
  createdAt: isoDateTimeSchema,
});
export type WorkflowTemplate = z.infer<typeof workflowTemplateSchema>;

/** Configuração de uma fase dentro de uma versão de workflow. */
export const workflowPhaseConfigSchema = z.object({
  /** Tipos de documento esperados na fase. */
  documentKinds: z.array(documentKindSchema),
  /** Peso da fase no progresso global (0–100). */
  progressWeight: z.number().min(0).max(100),
  /** Definições de agente permitidas na fase (vazio = todas). */
  allowedAgentDefinitionIds: z.array(ulidSchema),
  /* ---- campos do editor de fases (FR-4 — todos opcionais/aditivos) ---- */
  /** Objetivo da fase (o que deve ser alcançado). */
  objective: z.string().optional(),
  /** Contexto/insumos relevantes para os agentes da fase. */
  context: z.string().optional(),
  /** Critérios de aceite da fase. */
  acceptanceCriteria: z.array(z.string()).optional(),
  /** Fases das quais esta depende (nomes; sem ciclos — validado na publicação). */
  dependsOn: z.array(z.string()).optional(),
  /** Condições para entrar na fase. */
  entryConditions: z.array(z.string()).optional(),
  /** Condições para sair/concluir a fase. */
  exitConditions: z.array(z.string()).optional(),
  /** Skills permitidas na fase (catálogo `skills`; vazio/ausente = todas). */
  allowedSkillIds: z.array(ulidSchema).optional(),
  /** Ferramentas permitidas na fase (catálogo `tools`; vazio/ausente = todas). */
  allowedToolIds: z.array(ulidSchema).optional(),
});
export type WorkflowPhaseConfig = z.infer<typeof workflowPhaseConfigSchema>;

/**
 * Versão de um template de workflow. Publicada = imutável (alterar cria
 * NOVA versão); rascunho = editável até publicar; arquivada = tombstone
 * (versão utilizada por execução/vínculo nunca é excluída fisicamente).
 */
export const workflowVersionSchema = z.object({
  id: ulidSchema,
  templateId: ulidSchema,
  version: z.number().int().positive(),
  /** Nomes das fases em ordem. */
  phases: z.array(z.string()),
  /** Nomes dos gates por fase (fase → gates). */
  gatesByPhase: z.record(z.array(z.string())),
  /** Configuração por fase (documentos, peso de progresso, agentes). */
  phaseConfigs: z.record(workflowPhaseConfigSchema).optional(),
  /** Modo de operação sugerido ao vincular o template a um projeto. */
  defaultOperationMode: operationModeSchema.nullable().optional(),
  /** Regras de transição: fase → fases seguintes permitidas. */
  transitions: z.record(z.array(z.string())).optional(),
  changelog: z.string().nullable(),
  /** Ciclo de vida (FR-4, aditivo). */
  state: workflowContentStateSchema,
  /** Nulo enquanto rascunho; preenchido na publicação. */
  publishedAt: isoDateTimeSchema.nullable(),
  archivedAt: isoDateTimeSchema.nullable(),
});
export type WorkflowVersion = z.infer<typeof workflowVersionSchema>;

/** Registro de aceite de risco na troca de modo de operação. */
export const riskAcceptanceSchema = z.object({
  mode: operationModeSchema,
  acceptedByProfileId: ulidSchema,
  note: z.string(),
  acceptedAt: isoDateTimeSchema,
});
export type RiskAcceptance = z.infer<typeof riskAcceptanceSchema>;

/** Workflow vinculado a um projeto (versão ativa + modo de operação). */
export const workflowSchema = z.object({
  id: ulidSchema,
  projectId: ulidSchema,
  templateId: ulidSchema,
  activeVersionId: ulidSchema,
  operationMode: operationModeSchema,
  /** No modo semiautônomo: quais gates pausam para aprovação humana. */
  semiautonomousPauseGates: z.array(z.string()),
  riskAcceptances: z.array(riskAcceptanceSchema),
  createdAt: isoDateTimeSchema,
});
export type Workflow = z.infer<typeof workflowSchema>;

export const workflowRunSchema = z.object({
  id: ulidSchema,
  workflowId: ulidSchema,
  versionId: ulidSchema,
  state: workflowRunStateSchema,
  startedAt: isoDateTimeSchema,
  finishedAt: isoDateTimeSchema.nullable(),
});
export type WorkflowRun = z.infer<typeof workflowRunSchema>;

export const phaseProgressBreakdownSchema = z.object({
  completed: z.number().int().nonnegative(),
  total: z.number().int().nonnegative(),
});

export const phaseProgressSchema = z.object({
  completed: z.number().int().nonnegative(),
  total: z.number().int().nonnegative(),
  percent: z.number().min(0).max(100),
  source: z.enum([
    'phase_obligation_plan',
    'workflow_run_objectives_and_gates',
    'workflow_run_unavailable',
  ]),
  updatedAt: isoDateTimeSchema.nullable(),
  tasks: phaseProgressBreakdownSchema,
  documents: phaseProgressBreakdownSchema,
  gates: phaseProgressBreakdownSchema,
});

/** Obrigação materializada que compõe o progresso aceito de uma fase. */
export const phaseObligationSchema = z.object({
  obligationKey: z.string(),
  kind: z.string(),
  description: z.string(),
  required: z.boolean(),
  weight: z.number().nonnegative(),
  state: z.string(),
  source: z.string(),
  cardId: ulidSchema.nullable(),
  objectiveKey: z.string().nullable(),
  artifactRef: z.string().nullable(),
  evidence: z.array(z.string()),
  reason: z.string().nullable(),
});
export type PhaseObligation = z.infer<typeof phaseObligationSchema>;

/**
 * Fonte canônica do Dashboard: mede somente obrigações aceitas no percentual.
 * Trabalho em voo e decisão humana permanecem eixos independentes.
 */
export const phaseObligationProgressSchema = z.object({
  runId: ulidSchema,
  phaseKey: z.string(),
  planVersion: z.number().int().positive(),
  percentage: z.coerce.number().min(0).max(100),
  requiredTotal: z.number().int().nonnegative(),
  requiredAccepted: z.number().int().nonnegative(),
  inProgress: z.number().int().nonnegative(),
  inReview: z.number().int().nonnegative(),
  blocked: z.number().int().nonnegative(),
  pending: z.number().int().nonnegative(),
  optionalTotal: z.number().int().nonnegative(),
  optionalAccepted: z.number().int().nonnegative(),
  technicallyComplete: z.boolean(),
  obligations: z.array(phaseObligationSchema),
});
export type PhaseObligationProgress = z.infer<typeof phaseObligationProgressSchema>;

export const phaseDeliverableSchema = z.object({
  name: z.string(),
  status: z.enum(['planned', 'notStarted', 'inProduction', 'inReview', 'approved', 'rejected']),
});

export const phaseSchema = z.object({
  id: ulidSchema,
  runId: ulidSchema,
  name: z.string(),
  order: z.number().int().positive(),
  state: phaseStateSchema,
  startedAt: isoDateTimeSchema.nullable(),
  finishedAt: isoDateTimeSchema.nullable(),
  /** Read model canônico do run; consumido por Dashboard, Chat e Workflows. */
  progress: phaseProgressSchema,
  /** Entregáveis previstos persistidos como objetivos documentais do run. */
  deliverables: z.array(phaseDeliverableSchema),
});
export type Phase = z.infer<typeof phaseSchema>;

export const gateSchema = z.object({
  id: ulidSchema,
  phaseId: ulidSchema,
  runId: ulidSchema,
  name: z.string(),
  state: gateStateSchema,
  requiresApproval: z.boolean(),
  decidedByProfileId: ulidSchema.nullable(),
  decidedAt: isoDateTimeSchema.nullable(),
  /** Observação — obrigatória em reprovação. */
  note: z.string().nullable(),
});
export type Gate = z.infer<typeof gateSchema>;

/**
 * Aprovação humana (gate, documento, mudança de modo...).
 * Reprovação EXIGE `resolutionNote`.
 */
export const approvalSchema = z
  .object({
    id: ulidSchema,
    projectId: ulidSchema,
    gateId: ulidSchema.nullable(),
    taskId: ulidSchema.nullable(),
    documentId: ulidSchema.nullable(),
    title: z.string(),
    description: z.string(),
    /** Criticidade da decisão (fila ordena por prazo → criticidade). */
    priority: prioritySchema,
    /** Prazo limite para decidir (null = sem prazo). */
    dueAt: isoDateTimeSchema.nullable(),
    state: approvalStateSchema,
    requestedByAgentId: ulidSchema,
    requestedAt: isoDateTimeSchema,
    resolvedByProfileId: ulidSchema.nullable(),
    resolvedAt: isoDateTimeSchema.nullable(),
    resolutionNote: z.string().nullable(),
    /**
     * Projeção preparada pelo backend para decisão segura em modo de negócio.
     * Ausência mantém o detalhe técnico intacto, mas bloqueia a resolução nessa visão.
     */
    businessTitle: z.string().nullable(),
    businessDescription: z.string().nullable(),
  })
  .refine((a) => a.state !== 'rejected' || (a.resolutionNote?.length ?? 0) > 0, {
    message: 'Reprovação exige observação (resolutionNote).',
    path: ['resolutionNote'],
  });
export type Approval = z.infer<typeof approvalSchema>;
