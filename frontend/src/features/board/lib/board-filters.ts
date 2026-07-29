import { taskStateSchema, type Task, type TaskState } from '@/api';

/**
 * Filtros da barra do quadro (FR-3, item 7.1). Estado vive na URL:
 * - `?state=` filtra a coluna — MESMO param do deep-link do cockpit
 *   (destaque/rolagem), compatibilidade preservada (D-074);
 * - `?phase=`, `?period=` e `?archive=` completam o conjunto enxuto;
 * - `?task=` (detalhe aberto) é preservado por todas as operações.
 */

export type BoardPeriod = 'today' | '7d' | '30d' | 'all';
export type BoardArchiveFilter = 'active' | 'archived' | 'all';
export interface BoardFilters {
  /** Coluna filtrada ('' = todas). */
  state: TaskState | '';
  /** Etapa do fluxo ('' = todas). */
  phase: string;
  /** Janela sobre a ÚLTIMA ATIVIDADE (updatedAt). */
  period: BoardPeriod;
  /** Padrão: ativas (arquivadas fora do quadro). */
  archive: BoardArchiveFilter;
}

export const DEFAULT_BOARD_FILTERS: BoardFilters = {
  state: '',
  phase: '',
  period: 'all',
  archive: 'active',
};

const PERIODS: readonly BoardPeriod[] = ['today', '7d', '30d', 'all'];
const ARCHIVE_FILTERS: readonly BoardArchiveFilter[] = ['active', 'archived', 'all'];
const RETIRED_FILTER_KEYS = [
  'q',
  'agent',
  'signature',
  'specialty',
  'type',
  'priority',
] as const;

function parseEnum<T extends string>(value: string | null, options: readonly T[], fallback: T): T {
  return value !== null && (options as readonly string[]).includes(value) ? (value as T) : fallback;
}

/** Lê os filtros da URL, validando contra os enums do contrato. */
export function parseBoardFilters(searchParams: URLSearchParams): BoardFilters {
  const stateParam = searchParams.get('state');
  const parsedState = stateParam === null ? null : taskStateSchema.safeParse(stateParam);
  return {
    state: parsedState?.success ? parsedState.data : '',
    phase: searchParams.get('phase') ?? '',
    period: parseEnum(searchParams.get('period'), PERIODS, 'all'),
    archive: parseEnum(searchParams.get('archive'), ARCHIVE_FILTERS, 'active'),
  };
}

/**
 * Escreve os filtros na URL sobre os params atuais (preserva `?task=`).
 * Valores padrão são removidos — `/board` limpo permanece deep-linkável.
 */
export function boardFiltersToSearchParams(
  previous: URLSearchParams,
  filters: BoardFilters,
): URLSearchParams {
  const next = new URLSearchParams(previous);
  for (const key of RETIRED_FILTER_KEYS) next.delete(key);
  const setOrDelete = (key: string, value: string, isDefault: boolean) => {
    if (isDefault || value === '') next.delete(key);
    else next.set(key, value);
  };
  setOrDelete('state', filters.state, filters.state === '');
  setOrDelete('phase', filters.phase, filters.phase === '');
  setOrDelete('period', filters.period, filters.period === 'all');
  setOrDelete('archive', filters.archive, filters.archive === 'active');
  return next;
}

/** Há algum filtro fora do padrão? (habilita "limpar filtros"). */
export function hasActiveBoardFilters(filters: BoardFilters): boolean {
  return (
    filters.state !== '' ||
    filters.phase !== '' ||
    filters.period !== 'all' ||
    filters.archive !== 'active'
  );
}

const DAY_MS = 24 * 60 * 60 * 1000;

/** Limite inferior da janela de período sobre `updatedAt` (null = sem corte). */
export function periodCutoff(period: BoardPeriod, now: Date): Date | null {
  switch (period) {
    case 'all':
      return null;
    case 'today': {
      const start = new Date(now);
      start.setHours(0, 0, 0, 0);
      return start;
    }
    case '7d':
      return new Date(now.getTime() - 7 * DAY_MS);
    case '30d':
      return new Date(now.getTime() - 30 * DAY_MS);
  }
}

/**
 * Aplica os filtros sobre as tarefas do projeto. Arquivamento é metaestado:
 * o padrão ("ativas") esconde as arquivadas sem apagar estado nem histórico.
 */
export function filterBoardTasks(tasks: Task[], filters: BoardFilters, now: Date): Task[] {
  const cutoff = periodCutoff(filters.period, now);
  return tasks.filter((task) => {
    if (filters.archive === 'active' && task.archivedAt !== null) return false;
    if (filters.archive === 'archived' && task.archivedAt === null) return false;
    if (filters.state !== '' && task.state !== filters.state) return false;
    if (filters.phase !== '' && task.phaseName !== filters.phase) return false;
    if (cutoff !== null && new Date(task.updatedAt) < cutoff) return false;
    return true;
  });
}

const STUCK_THRESHOLD_MS = 2 * 60 * 60 * 1000;
const ACTIVE_WORK_STATES: ReadonlySet<TaskState> = new Set([
  'development',
  'review',
  'corrections',
  'testsGates',
]);

/**
 * Sinal conservador de estagnação visível: trabalho ativo sem atualização por
 * duas horas. Não altera estado e não presume falha de processo/lease.
 */
export function isTaskStuck(task: Task, now: Date): boolean {
  return (
    ACTIVE_WORK_STATES.has(task.state) &&
    now.getTime() - new Date(task.updatedAt).getTime() >= STUCK_THRESHOLD_MS
  );
}
