import type {
  AccountState,
  AccountHealth,
  AgentState,
  ApprovalState,
  AttemptState,
  AuditActorKind,
  ChiefTurnState,
  ComponentState,
  ConversationState,
  DocumentState,
  GateState,
  LicenseState,
  NotificationSeverity,
  NotificationStatus,
  OperationMode,
  PhaseState,
  Priority,
  ProjectState,
  PrototypeState,
  RunTargetState,
  TaskState,
  WorkflowContentState,
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

/** Ciclo de vida de template/versão de workflow (rascunho/publicado/arquivado). */
export const WORKFLOW_CONTENT_STATE_VARIANTS: Record<WorkflowContentState, BadgeProps['variant']> = {
  draft: 'warning',
  published: 'success',
  archived: 'outline',
};

export function workflowContentStateVariant(state: WorkflowContentState): BadgeProps['variant'] {
  return WORKFLOW_CONTENT_STATE_VARIANTS[state];
}

/** Estado do turno do chefe: atenção só quando aguarda aprovação humana. */
export const CHIEF_TURN_STATE_VARIANTS: Record<ChiefTurnState, BadgeProps['variant']> = {
  pending: 'warning',
  processing: 'brand',
  completed: 'success',
  failed: 'error',
  blocked: 'warning',
};

export function chiefTurnStateVariant(state: ChiefTurnState): BadgeProps['variant'] {
  return CHIEF_TURN_STATE_VARIANTS[state];
}

/** Ferramentas, skills, plugins e MCP: habilitado é sucesso, erro é erro. */
export const COMPONENT_STATE_VARIANTS: Record<ComponentState, BadgeProps['variant']> = {
  enabled: 'success',
  disabled: 'outline',
  error: 'error',
};

export function componentStateVariant(state: ComponentState): BadgeProps['variant'] {
  return COMPONENT_STATE_VARIANTS[state];
}

/** Atores de auditoria: humano em destaque, sistema neutro. */
export const AUDIT_ACTOR_KIND_VARIANTS: Record<AuditActorKind, BadgeProps['variant']> = {
  user: 'brand',
  chief: 'info',
  agent: 'default',
  system: 'outline',
};

export function auditActorKindVariant(kind: AuditActorKind): BadgeProps['variant'] {
  return AUDIT_ACTOR_KIND_VARIANTS[kind];
}

/** Severidade de notificação: critical usa a variante de erro (mais grave). */
export const NOTIFICATION_SEVERITY_VARIANTS: Record<NotificationSeverity, BadgeProps['variant']> = {
  info: 'info',
  warning: 'warning',
  error: 'error',
  critical: 'error',
};

export function notificationSeverityVariant(
  severity: NotificationSeverity,
): BadgeProps['variant'] {
  return NOTIFICATION_SEVERITY_VARIANTS[severity];
}

/** Status de notificação: não lida chama atenção (brand), silenciada é atenção. */
export const NOTIFICATION_STATUS_VARIANTS: Record<NotificationStatus, BadgeProps['variant']> = {
  unread: 'brand',
  read: 'outline',
  muted: 'warning',
};

export function notificationStatusVariant(status: NotificationStatus): BadgeProps['variant'] {
  return NOTIFICATION_STATUS_VARIANTS[status];
}

/** Serviços detectados: em execução é info, parado neutro, desconhecido atenção. */
export const RUN_TARGET_STATE_VARIANTS: Record<RunTargetState, BadgeProps['variant']> = {
  running: 'info',
  stopped: 'outline',
  unknown: 'warning',
};

export function runTargetStateVariant(state: RunTargetState): BadgeProps['variant'] {
  return RUN_TARGET_STATE_VARIANTS[state];
}

/** Licença: ativa é sucesso; tolerância/offline atenção; expirada/sem licença erro. */
export const LICENSE_STATE_VARIANTS: Record<LicenseState, BadgeProps['variant']> = {
  active: 'success',
  gracePeriod: 'warning',
  expired: 'error',
  offline: 'warning',
  unlicensed: 'error',
};

export function licenseStateVariant(state: LicenseState): BadgeProps['variant'] {
  return LICENSE_STATE_VARIANTS[state];
}

/** Conta de provider: ativa é sucesso, cota excedida é erro. */
export const ACCOUNT_STATE_VARIANTS: Record<AccountState, BadgeProps['variant']> = {
  active: 'success',
  disabled: 'outline',
  quotaExceeded: 'error',
};

export const ACCOUNT_HEALTH_VARIANTS: Record<AccountHealth, BadgeProps['variant']> = {
  unknown: 'outline',
  healthy: 'success',
  degraded: 'warning',
  unavailable: 'error',
};

export function accountHealthVariant(health: AccountHealth): BadgeProps['variant'] {
  return ACCOUNT_HEALTH_VARIANTS[health];
}

export function accountStateVariant(state: AccountState): BadgeProps['variant'] {
  return ACCOUNT_STATE_VARIANTS[state];
}

/** Protótipos: publicado é sucesso, gerando é info, rascunho neutro. */
export const PROTOTYPE_STATE_VARIANTS: Record<PrototypeState, BadgeProps['variant']> = {
  draft: 'outline',
  generating: 'info',
  ready: 'brand',
  published: 'success',
  archived: 'outline',
};

export function prototypeStateVariant(state: PrototypeState): BadgeProps['variant'] {
  return PROTOTYPE_STATE_VARIANTS[state];
}

export const CONVERSATION_STATE_VARIANTS: Record<ConversationState, BadgeProps['variant']> = {
  active: 'info',
  archived: 'outline',
};

export function conversationStateVariant(state: ConversationState): BadgeProps['variant'] {
  return CONVERSATION_STATE_VARIANTS[state];
}
