import {
  prioritySchema,
  taskStateSchema,
  type Priority,
  type Task,
  type TaskState,
} from '@/api';

/**
 * Filtros da barra do quadro (FR-3, item 7.1). Estado vive na URL:
 * - `?q=` busca por título e ID (normalizada, sem acentos);
 * - `?state=` filtra a coluna — MESMO param do deep-link do cockpit
 *   (destaque/rolagem), compatibilidade preservada (D-074);
 * - `?agent=`, `?priority=`, `?period=`, `?archive=`;
 * - `?task=` (detalhe aberto) é preservado por todas as operações.
 * Filtro por FASE não existe: `Task`/`Demand` não têm fase no contrato
 * (lacuna documentada no HANDOFF_API.md, D-075).
 */

export type BoardPeriod = 'today' | '7d' | '30d' | 'all';
export type BoardArchiveFilter = 'active' | 'archived' | 'all';

export interface BoardFilters {
  query: string;
  /** Coluna filtrada ('' = todas). */
  state: TaskState | '';
  /** Agente responsável ('' = todos). */
  agentId: string;
  priority: Priority | '';
  /** Janela sobre a ÚLTIMA ATIVIDADE (updatedAt). */
  period: BoardPeriod;
  /** Padrão: ativas (arquivadas fora do quadro). */
  archive: BoardArchiveFilter;
}

export const DEFAULT_BOARD_FILTERS: BoardFilters = {
  query: '',
  state: '',
  agentId: '',
  priority: '',
  period: 'all',
  archive: 'active',
};

const PERIODS: readonly BoardPeriod[] = ['today', '7d', '30d', 'all'];
const ARCHIVE_FILTERS: readonly BoardArchiveFilter[] = ['active', 'archived', 'all'];

function parseEnum<T extends string>(value: string | null, options: readonly T[], fallback: T): T {
  return value !== null && (options as readonly string[]).includes(value)
    ? (value as T)
    : fallback;
}

/** Lê os filtros da URL, validando contra os enums do contrato. */
export function parseBoardFilters(searchParams: URLSearchParams): BoardFilters {
  const stateParam = searchParams.get('state');
  const parsedState = stateParam === null ? null : taskStateSchema.safeParse(stateParam);
  const priorityParam = searchParams.get('priority');
  const parsedPriority =
    priorityParam === null ? null : prioritySchema.safeParse(priorityParam);
  return {
    query: searchParams.get('q') ?? '',
    state: parsedState?.success ? parsedState.data : '',
    agentId: searchParams.get('agent') ?? '',
    priority: parsedPriority?.success ? parsedPriority.data : '',
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
  const setOrDelete = (key: string, value: string, isDefault: boolean) => {
    if (isDefault || value === '') next.delete(key);
    else next.set(key, value);
  };
  // A busca NÃO é aparada aqui — espaços intermediários fazem parte da
  // digitação (o trim acontece na aplicação do filtro e no teste de default).
  setOrDelete('q', filters.query, filters.query.trim() === '');
  setOrDelete('state', filters.state, filters.state === '');
  setOrDelete('agent', filters.agentId, filters.agentId === '');
  setOrDelete('priority', filters.priority, filters.priority === '');
  setOrDelete('period', filters.period, filters.period === 'all');
  setOrDelete('archive', filters.archive, filters.archive === 'active');
  return next;
}

/** Há algum filtro fora do padrão? (habilita "limpar filtros"). */
export function hasActiveBoardFilters(filters: BoardFilters): boolean {
  return (
    filters.query.trim() !== '' ||
    filters.state !== '' ||
    filters.agentId !== '' ||
    filters.priority !== '' ||
    filters.period !== 'all' ||
    filters.archive !== 'active'
  );
}

/** Normalização para busca: minúsculas + sem acentos (NFD). */
export function normalizeBoardQuery(value: string): string {
  return value
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase();
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
  const query = normalizeBoardQuery(filters.query.trim());
  const cutoff = periodCutoff(filters.period, now);
  return tasks.filter((task) => {
    if (filters.archive === 'active' && task.archivedAt !== null) return false;
    if (filters.archive === 'archived' && task.archivedAt === null) return false;
    if (filters.state !== '' && task.state !== filters.state) return false;
    if (filters.agentId !== '' && task.assigneeAgentId !== filters.agentId) return false;
    if (filters.priority !== '' && task.priority !== filters.priority) return false;
    if (cutoff !== null && new Date(task.updatedAt) < cutoff) return false;
    if (query !== '') {
      const haystack = normalizeBoardQuery(`${task.title} ${task.id}`);
      if (!haystack.includes(query)) return false;
    }
    return true;
  });
}
