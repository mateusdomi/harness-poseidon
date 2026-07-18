import { z } from 'zod';

import { documentStateSchema, operationModeSchema, prioritySchema, solicitationStateSchema, taskStateSchema } from './enums';
import { workflowPhaseConfigSchema } from './delivery';

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
