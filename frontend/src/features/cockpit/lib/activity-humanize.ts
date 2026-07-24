import type { AuditEvent } from '@/api';

/**
 * Humanização de eventos de auditoria.
 *
 * LIMITE DE CONTRATO: `AuditEventContract.action` e `targetType` são strings
 * abertas — o OpenAPI não publica enum nem rótulo legível. Por isso o
 * mapeamento aqui é best-effort sobre o vocabulário `objeto.verbo` realmente
 * observado, com degradação graciosa: código desconhecido continua sendo
 * exibido, nunca substituído por texto inventado. O código cru permanece
 * sempre disponível no detalhe técnico ("Ver detalhes"). A ausência de um
 * catálogo canônico de ações está registrada em `docs/frontend/HANDOFF_API.md`.
 */

/** Resultado semântico do evento — dirige ícone e variante do badge. */
export type ActivityOutcome = 'success' | 'error' | 'warning' | 'neutral';

export interface HumanizedActivity {
  /** Chave i18n do rótulo humano, quando a ação é conhecida. */
  labelKey: string | null;
  /** Código cru (`project.created`) — detalhe técnico e fallback. */
  rawAction: string;
  /** Tipo do objeto (`project`), quando conhecido. */
  targetType: string;
  outcome: ActivityOutcome;
  /** Rota do objeto, quando existe tela correspondente. */
  link: string | null;
}

/**
 * Ações com rótulo humano publicado no i18n (`cockpit.activity.actions.*`).
 * Mantida explícita: acrescentar uma ação exige acrescentar a tradução.
 */
export const KNOWN_ACTIONS = [
  'agent.error',
  'approval.resolved',
  'approval.requested',
  'demand.created',
  'demand.completed',
  'license.validated',
  'solicitation.created',
  'task.created',
  'task.stateChanged',
  'task.completed',
  'document.created',
  'document.approved',
  'document.stateChanged',
  'workflow.operationModeChanged',
  'workflow.versionPublished',
  'project.created',
  'organization.created',
  'profile.settingsUpdated',
  'conversation.created',
  'workflow.linked',
] as const;

const KNOWN_ACTION_SET = new Set<string>(KNOWN_ACTIONS);

/**
 * Vaivém técnico interno que NÃO interessa ao dono da fábrica: troca de estado
 * do turno do chefe, mensagens anexadas, heartbeats, progresso incremental,
 * leases de orquestração. O feed de atividade é "o que mudou a operação", não
 * o log de execução (esse vive na Governança). Marcamos por SUBSTRING do
 * `action`/`targetType` porque o contrato de auditoria é de string aberta.
 */
const TECHNICAL_NOISE_MATCHERS = [
  'turnstate',
  'turn.',
  'message.appended',
  'message.',
  'heartbeat',
  'progress.updated',
  'progress.recomputed',
  'lease',
  'fencing',
  'probe',
  'heartbeated',
  '.synced',
  '.ping',
  'chiefturn',
] as const;

const NOISE_TARGET_TYPES = new Set<string>([
  'chiefTurn',
  'turn',
  'message',
  'heartbeat',
  'lease',
]);

/**
 * Um evento é "significativo" para o dono quando NÃO é vaivém técnico. Regra
 * conservadora: reprova apenas o que casa o denylist; qualquer evento de
 * negócio (tarefa, documento, aprovação, demanda, conversa, agente, projeto)
 * passa e é humanizado normalmente. Nunca inventamos texto — só escondemos
 * ruído (o histórico completo continua na Governança).
 */
export function isMeaningfulActivity(event: AuditEvent): boolean {
  if (NOISE_TARGET_TYPES.has(event.targetType)) return false;
  const action = event.action.toLowerCase();
  return !TECHNICAL_NOISE_MATCHERS.some((needle) => action.includes(needle));
}

/** Sufixos de verbo que indicam falha/atenção — usados só como heurística. */
const ERROR_SUFFIXES = ['error', 'failed', 'rejected', 'cancelled'];
const WARNING_SUFFIXES = ['blocked', 'exceeded', 'degraded', 'expired'];

export function deriveOutcome(action: string): ActivityOutcome {
  const verb = action.split('.').pop()?.toLowerCase() ?? '';
  if (ERROR_SUFFIXES.some((suffix) => verb.includes(suffix))) return 'error';
  if (WARNING_SUFFIXES.some((suffix) => verb.includes(suffix))) return 'warning';
  if (verb === '') return 'neutral';
  return 'success';
}

/** Rotas por tipo de objeto. Só mapeamos telas que realmente existem. */
const TARGET_ROUTES: Record<string, string> = {
  task: '/board',
  approval: '/approvals',
  demand: '/board',
  solicitation: '/board',
  agent: '/agents',
  workflow: '/workflows',
  project: '/projects',
  organization: '/organizations',
  license: '/licenses',
  conversation: '/conversations',
  document: '/documents',
};

export function humanizeActivity(event: AuditEvent): HumanizedActivity {
  const known = KNOWN_ACTION_SET.has(event.action);
  return {
    labelKey: known ? `cockpit.activity.actions.${event.action}` : null,
    rawAction: event.action,
    targetType: event.targetType,
    outcome: deriveOutcome(event.action),
    link: TARGET_ROUTES[event.targetType] ?? null,
  };
}
