import type {
  AgentState,
  ApprovalState,
  AttemptState,
  DocumentState,
  GateState,
  OperationMode,
  PhaseState,
  Priority,
  ProjectState,
  TaskState,
  WorkflowRunState,
} from '@/api';
import type { BadgeProps } from '@/design-system';

/**
 * Mapeamento tipado de enums do domínio → variantes semânticas do Badge.
 * NENHUMA cor/status hardcoded nos componentes: toda tela passa por aqui.
 * Os labels ficam no i18n (`status.projectState.*`, `status.priority.*`).
 */

export const PROJECT_STATE_VARIANTS: Record<ProjectState, BadgeProps['variant']> = {
  active: 'success',
  paused: 'warning',
  archived: 'outline',
};

export const PRIORITY_VARIANTS: Record<Priority, BadgeProps['variant']> = {
  low: 'outline',
  medium: 'info',
  high: 'warning',
  critical: 'error',
};

export function projectStateVariant(state: ProjectState): BadgeProps['variant'] {
  return PROJECT_STATE_VARIANTS[state];
}

export function priorityVariant(priority: Priority): BadgeProps['variant'] {
  return PRIORITY_VARIANTS[priority];
}

/** Colunas do quadro: `blocked` é erro, trabalho ativo é info, done é sucesso. */
export const TASK_STATE_VARIANTS: Record<TaskState, BadgeProps['variant']> = {
  backlog: 'outline',
  ready: 'default',
  development: 'info',
  review: 'warning',
  corrections: 'warning',
  testsGates: 'brand',
  blocked: 'error',
  done: 'success',
};

export function taskStateVariant(state: TaskState): BadgeProps['variant'] {
  return TASK_STATE_VARIANTS[state];
}

export const AGENT_STATE_VARIANTS: Record<AgentState, BadgeProps['variant']> = {
  working: 'info',
  idle: 'outline',
  waiting: 'warning',
  error: 'error',
  outOfQuota: 'error',
};

export function agentStateVariant(state: AgentState): BadgeProps['variant'] {
  return AGENT_STATE_VARIANTS[state];
}

/** Tentativas: running é info, completed sucesso, failed erro. */
export const ATTEMPT_STATE_VARIANTS: Record<AttemptState, BadgeProps['variant']> = {
  queued: 'outline',
  running: 'info',
  completed: 'success',
  failed: 'error',
  cancelled: 'outline',
};

export function attemptStateVariant(state: AttemptState): BadgeProps['variant'] {
  return ATTEMPT_STATE_VARIANTS[state];
}

export const APPROVAL_STATE_VARIANTS: Record<ApprovalState, BadgeProps['variant']> = {
  pending: 'warning',
  approved: 'success',
  rejected: 'error',
  cancelled: 'outline',
};

export function approvalStateVariant(state: ApprovalState): BadgeProps['variant'] {
  return APPROVAL_STATE_VARIANTS[state];
}

export const GATE_STATE_VARIANTS: Record<GateState, BadgeProps['variant']> = {
  pending: 'warning',
  approved: 'success',
  rejected: 'error',
  waived: 'outline',
};

export function gateStateVariant(state: GateState): BadgeProps['variant'] {
  return GATE_STATE_VARIANTS[state];
}

export const PHASE_STATE_VARIANTS: Record<PhaseState, BadgeProps['variant']> = {
  pending: 'outline',
  active: 'info',
  completed: 'success',
  skipped: 'outline',
  failed: 'error',
};

export function phaseStateVariant(state: PhaseState): BadgeProps['variant'] {
  return PHASE_STATE_VARIANTS[state];
}

/** Documentos: atenção em revisão/aprovação, sucesso só no aprovado. */
export const DOCUMENT_STATE_VARIANTS: Record<DocumentState, BadgeProps['variant']> = {
  planned: 'outline',
  inElaboration: 'info',
  inReview: 'warning',
  awaitingApproval: 'warning',
  approved: 'success',
  outdated: 'error',
  superseded: 'outline',
  notApplicable: 'outline',
};

export function documentStateVariant(state: DocumentState): BadgeProps['variant'] {
  return DOCUMENT_STATE_VARIANTS[state];
}

export const OPERATION_MODE_VARIANTS: Record<OperationMode, BadgeProps['variant']> = {
  manual: 'outline',
  semiautonomous: 'warning',
  autonomous: 'brand',
};

export function operationModeVariant(mode: OperationMode): BadgeProps['variant'] {
  return OPERATION_MODE_VARIANTS[mode];
}

export const WORKFLOW_RUN_STATE_VARIANTS: Record<WorkflowRunState, BadgeProps['variant']> = {
  running: 'info',
  paused: 'warning',
  completed: 'success',
  failed: 'error',
  cancelled: 'outline',
};

export function workflowRunStateVariant(state: WorkflowRunState): BadgeProps['variant'] {
  return WORKFLOW_RUN_STATE_VARIANTS[state];
}
