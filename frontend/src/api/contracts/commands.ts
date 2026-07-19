import { z } from 'zod';

import { documentStateSchema, operationModeSchema, prioritySchema, solicitationStateSchema, taskStateSchema } from './enums';
import { workflowPhaseConfigSchema } from './delivery';
import { isoDateTimeSchema, ulidSchema } from './primitives';

/**
 * Comandos de domínio (POSTs fora do CRUD) — espelhados no backend.
 * Tudo que muda estado fora do CRUD passa por aqui, preservando as
 * regras de imutabilidade (sem PUT em tarefa/solicitação/instrução).
 */

/** Move tarefa entre colunas do quadro. */
export const moveTaskInputSchema = z.object({
  toState: taskStateSchema,
  note: z.string().optional(),
});
export type MoveTaskInput = z.infer<typeof moveTaskInputSchema>;

/** Altera a prioridade da tarefa (ação humana permitida — não é edição de conteúdo). */
export const setTaskPriorityInputSchema = z.object({
  priority: prioritySchema,
});
export type SetTaskPriorityInput = z.infer<typeof setTaskPriorityInputSchema>;

/** Nova versão de instrução de tarefa (correção — nunca edita a anterior). */
export const appendTaskInstructionInputSchema = z.object({
  body: z.string().min(1),
});
export type AppendTaskInstructionInput = z.infer<typeof appendTaskInstructionInputSchema>;

/** Triagem de solicitação (muda apenas o estado — conteúdo é imutável). */
export const transitionSolicitationInputSchema = z.object({
  state: solicitationStateSchema,
});
export type TransitionSolicitationInput = z.infer<typeof transitionSolicitationInputSchema>;

/** Resolve aprovação; reprovação EXIGE observação. */
export const resolveApprovalInputSchema = z
  .object({
    decision: z.enum(['approved', 'rejected']),
    note: z.string().optional(),
  })
  .refine((i) => i.decision !== 'rejected' || (i.note?.length ?? 0) > 0, {
    message: 'Reprovação exige observação (note).',
    path: ['note'],
  });
export type ResolveApprovalInput = z.infer<typeof resolveApprovalInputSchema>;

/** Transição de estado de documento. */
export const transitionDocumentInputSchema = z.object({
  toState: documentStateSchema,
  note: z.string().optional(),
});
export type TransitionDocumentInput = z.infer<typeof transitionDocumentInputSchema>;

/**
 * Nova versão de documento por edição MANUAL (revisão humana): versões são
 * imutáveis — salvar cria `currentVersion + 1` com `authorKind: 'user'`,
 * nunca edita a anterior.
 */
export const saveDocumentVersionInputSchema = z.object({
  body: z.string().min(1),
});
export type SaveDocumentVersionInput = z.infer<typeof saveDocumentVersionInputSchema>;

/**
 * Classificação de documento (metadados — não é edição de conteúdo):
 * rótulos e vínculo de fase. Usada também para "adotar" documentos órfãos.
 */
export const classifyDocumentInputSchema = z.object({
  classifications: z.array(z.string()).optional(),
  phaseName: z.string().nullable().optional(),
});
export type ClassifyDocumentInput = z.infer<typeof classifyDocumentInputSchema>;

/**
 * Nova versão de template de workflow — já nasce publicada (imutável) e
 * emite `workflow.versionPublished`. Fases na ordem de execução.
 */
export const publishWorkflowVersionInputSchema = z.object({
  phases: z.array(z.string().min(1)).min(1),
  gatesByPhase: z.record(z.array(z.string())),
  phaseConfigs: z.record(workflowPhaseConfigSchema).optional(),
  defaultOperationMode: operationModeSchema.nullable().optional(),
  transitions: z.record(z.array(z.string())).optional(),
  changelog: z.string().optional(),
});
export type PublishWorkflowVersionInput = z.infer<typeof publishWorkflowVersionInputSchema>;

/* ---- gestão de templates de workflow (FR-4) ---- */

/** Criação de template do zero — nasce como rascunho (sem versão publicada). */
export const createWorkflowTemplateInputSchema = z.object({
  name: z.string().min(1),
  description: z.string().optional(),
});
export type CreateWorkflowTemplateInput = z.infer<typeof createWorkflowTemplateInputSchema>;

/**
 * Conteúdo de uma versão em rascunho (criação/edição). Igual ao payload de
 * publicação, mas tudo opcional — rascunho admite estado incompleto; a
 * validação completa (zod + regras do Harness) acontece ao publicar.
 */
export const workflowDraftInputSchema = publishWorkflowVersionInputSchema.partial();
export type WorkflowDraftInput = z.infer<typeof workflowDraftInputSchema>;

/** Publicação de um rascunho existente (changelog opcional de última hora). */
export const publishWorkflowDraftInputSchema = z.object({
  changelog: z.string().optional(),
});
export type PublishWorkflowDraftInput = z.infer<typeof publishWorkflowDraftInputSchema>;

/**
 * Vincula um template a um projeto (cria o `Workflow` do projeto com a
 * versão publicada vigente do template). 409 se o projeto já tem workflow.
 */
export const linkWorkflowTemplateInputSchema = z.object({
  projectId: ulidSchema,
  templateId: ulidSchema,
  /** Versão específica a vincular; default = `currentVersionId` do template. */
  versionId: ulidSchema.optional(),
});
export type LinkWorkflowTemplateInput = z.infer<typeof linkWorkflowTemplateInputSchema>;

/**
 * Troca de modo de operação do workflow — exige confirmação com
 * registro de aceite de risco. No semiautônomo, define quais gates pausam.
 */
export const setOperationModeInputSchema = z.object({
  mode: operationModeSchema,
  semiautonomousPauseGates: z.array(z.string()).optional(),
  riskAcceptanceNote: z.string().min(1),
});
export type SetOperationModeInput = z.infer<typeof setOperationModeInputSchema>;

/** Inicia um turno do chefe numa conversa (resposta chega via realtime). */
export const startChatTurnInputSchema = z.object({
  content: z.string().min(1),
});
export type StartChatTurnInput = z.infer<typeof startChatTurnInputSchema>;

export const chatTurnHandleSchema = z.object({
  turnId: z.string(),
  conversationId: z.string(),
});
export type ChatTurnHandle = z.infer<typeof chatTurnHandleSchema>;

/**
 * Passagem de bastão do chefe (handoff): outra instância assume a
 * orquestração do projeto, opcionalmente com outra definição/modelo.
 * `note` (motivo) é obrigatória — vira registro de auditoria.
 */
export const handoffChiefInputSchema = z.object({
  targetDefinitionId: ulidSchema.nullable().optional(),
  targetModelId: ulidSchema.nullable().optional(),
  note: z.string().min(1),
});
export type HandoffChiefInput = z.infer<typeof handoffChiefInputSchema>;

/** Drenar tarefas do projeto: devolve tarefas em andamento para `ready`. */
export const drainChiefTasksInputSchema = z.object({
  note: z.string().optional(),
});
export type DrainChiefTasksInput = z.infer<typeof drainChiefTasksInputSchema>;

/* ---- rodar projeto (run-targets) ----
 * start/stop/restart não têm payload — POSTs de ação sobre o recurso.
 * O estado muda no `RunTarget` e os logs chegam por `run.logAppended`
 * no stream do projeto.
 */

/* ---- PO Assistant ---- */

/** Entrada da análise de solicitação (texto livre + anexos por nome). */
export const analyzeSolicitationInputSchema = z.object({
  projectId: ulidSchema,
  text: z.string().min(1),
  /** Nomes dos anexos (o conteúdo não sai do dispositivo no mock). */
  attachmentNames: z.array(z.string()).optional(),
});
export type AnalyzeSolicitationInput = z.infer<typeof analyzeSolicitationInputSchema>;

/** Item de um painel da análise (curadoria humana acontece na UI). */
export const solicitationAnalysisItemSchema = z.object({
  id: ulidSchema,
  text: z.string(),
});
export type SolicitationAnalysisItem = z.infer<typeof solicitationAnalysisItemSchema>;

/**
 * Resultado da análise: painéis de requisitos, ambiguidades,
 * contradições, perguntas e critérios de aceite, ligados à solicitação
 * criada (a demanda estruturada referencia `solicitationId`).
 */
export const solicitationAnalysisSchema = z.object({
  solicitationId: ulidSchema,
  requirements: z.array(solicitationAnalysisItemSchema),
  ambiguities: z.array(solicitationAnalysisItemSchema),
  contradictions: z.array(solicitationAnalysisItemSchema),
  questions: z.array(solicitationAnalysisItemSchema),
  acceptanceCriteria: z.array(solicitationAnalysisItemSchema),
});
export type SolicitationAnalysis = z.infer<typeof solicitationAnalysisSchema>;

/* ---- licença ---- */

/** Ativação de licença por chave (formato XXXX-XXXX-XXXX-XXXX). */
export const activateLicenseInputSchema = z.object({
  key: z
    .string()
    .regex(/^[A-Za-z0-9]{4}(-[A-Za-z0-9]{4}){3}$/, 'Formato esperado: XXXX-XXXX-XXXX-XXXX.'),
});
export type ActivateLicenseInput = z.infer<typeof activateLicenseInputSchema>;

/* ---- backup/restore e diagnóstico (settings) ---- */

export const backupHandleSchema = z.object({
  backupId: ulidSchema,
  createdAt: isoDateTimeSchema,
});
export type BackupHandle = z.infer<typeof backupHandleSchema>;

export const diagnosticCheckSchema = z.object({
  key: z.string(),
  state: z.enum(['ok', 'warning', 'error']),
  detail: z.string(),
});
export type DiagnosticCheck = z.infer<typeof diagnosticCheckSchema>;

/** Diagnóstico da instalação: versões, saúde e conexões. */
export const diagnosticsSchema = z.object({
  product: z.object({
    name: z.string(),
    version: z.string(),
    codename: z.string(),
  }),
  /** Modo da camada de dados (`mock` | `http`). */
  apiMode: z.string(),
  realtimeState: z.string(),
  checks: z.array(diagnosticCheckSchema),
  generatedAt: isoDateTimeSchema,
});
export type Diagnostics = z.infer<typeof diagnosticsSchema>;
