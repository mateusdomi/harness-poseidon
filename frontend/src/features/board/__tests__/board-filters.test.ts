import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  boardFiltersToSearchParams,
  DEFAULT_BOARD_FILTERS,
  filterBoardTasks,
  hasActiveBoardFilters,
  normalizeBoardQuery,
  parseBoardFilters,
  periodCutoff,
} from '@/features/board/lib/board-filters';
import {
  boardCsvFilename,
  boardTasksToCsv,
  summarizeAttemptsByTask,
} from '@/features/board/lib/board-export';

const fixtures = createTestBundle().fixtures.data;
const { tasks, attempts } = fixtures;
const NOW = new Date('2026-07-19T15:00:00Z');

describe('board-filters: parse e serialização da URL', () => {
  it('retorna padrões quando a URL não tem params (ativas, tudo, sem busca)', () => {
    expect(parseBoardFilters(new URLSearchParams())).toEqual(DEFAULT_BOARD_FILTERS);
  });

  it('valida enums e descarta valores inválidos', () => {
    const params = new URLSearchParams(
      'q=foo&state=blocked&priority=high&period=7d&archive=archived&agent=a1',
    );
    expect(parseBoardFilters(params)).toEqual({
      query: 'foo',
      state: 'blocked',
      agentId: 'a1',
      priority: 'high',
      period: '7d',
      archive: 'archived',
    });

    const invalid = new URLSearchParams('state=banana&priority=x&period=1y&archive=maybe');
    expect(parseBoardFilters(invalid)).toEqual(DEFAULT_BOARD_FILTERS);
  });

  it('serializa só valores fora do padrão e preserva ?task=', () => {
    const previous = new URLSearchParams('task=t1&state=blocked');
    const next = boardFiltersToSearchParams(previous, DEFAULT_BOARD_FILTERS);
    expect(next.get('task')).toBe('t1');
    expect(next.get('state')).toBeNull();
    expect(next.toString()).toBe('task=t1');

    const filled = boardFiltersToSearchParams(new URLSearchParams(), {
      ...DEFAULT_BOARD_FILTERS,
      query: ' billing ',
      archive: 'all',
      period: '30d',
    });
    expect(filled.get('q')).toBe(' billing ');
    expect(filled.get('archive')).toBe('all');
    expect(filled.get('period')).toBe('30d');
    expect(filled.get('priority')).toBeNull();
  });

  it('hasActiveBoardFilters detecta qualquer desvio do padrão', () => {
    expect(hasActiveBoardFilters(DEFAULT_BOARD_FILTERS)).toBe(false);
    expect(hasActiveBoardFilters({ ...DEFAULT_BOARD_FILTERS, query: 'x' })).toBe(true);
    expect(hasActiveBoardFilters({ ...DEFAULT_BOARD_FILTERS, archive: 'archived' })).toBe(true);
    expect(hasActiveBoardFilters({ ...DEFAULT_BOARD_FILTERS, period: 'today' })).toBe(true);
  });
});

describe('board-filters: filterBoardTasks', () => {
  const archivedTask = tasks.find((task) => task.archivedAt !== null)!;

  it('o padrão esconde arquivadas; "arquivadas" mostra só elas; "todas" não corta', () => {
    const active = filterBoardTasks(tasks, DEFAULT_BOARD_FILTERS, NOW);
    expect(active.every((task) => task.archivedAt === null)).toBe(true);
    expect(active).toHaveLength(tasks.length - 1);

    const archived = filterBoardTasks(
      tasks,
      { ...DEFAULT_BOARD_FILTERS, archive: 'archived' },
      NOW,
    );
    expect(archived).toEqual([archivedTask]);

    const all = filterBoardTasks(tasks, { ...DEFAULT_BOARD_FILTERS, archive: 'all' }, NOW);
    expect(all).toHaveLength(tasks.length);
  });

  it('busca por título (sem acento) e por ID', () => {
    expect(normalizeBoardQuery('Conciliação')).toBe('conciliacao');

    const byTitle = filterBoardTasks(
      tasks,
      { ...DEFAULT_BOARD_FILTERS, query: 'conciliacao' },
      NOW,
    );
    expect(byTitle.length).toBeGreaterThan(0);
    expect(byTitle.every((task) => normalizeBoardQuery(task.title).includes('conciliacao'))).toBe(
      true,
    );

    const byId = filterBoardTasks(
      tasks,
      { ...DEFAULT_BOARD_FILTERS, query: archivedTask.id },
      { ...NOW },
    );
    // A busca casa, mas o padrão "ativas" esconde a arquivada.
    expect(byId).toHaveLength(0);
    const byIdAll = filterBoardTasks(
      tasks,
      { ...DEFAULT_BOARD_FILTERS, query: archivedTask.id, archive: 'all' },
      NOW,
    );
    expect(byIdAll).toEqual([archivedTask]);
  });

  it('filtra por coluna, prioridade e responsável', () => {
    const blocked = filterBoardTasks(tasks, { ...DEFAULT_BOARD_FILTERS, state: 'blocked' }, NOW);
    expect(blocked.length).toBeGreaterThan(0);
    expect(blocked.every((task) => task.state === 'blocked')).toBe(true);

    const critical = filterBoardTasks(
      tasks,
      { ...DEFAULT_BOARD_FILTERS, priority: 'critical' },
      NOW,
    );
    expect(critical.every((task) => task.priority === 'critical')).toBe(true);

    const withAgent = tasks.find((task) => task.assigneeAgentId !== null)!;
    const byAgent = filterBoardTasks(
      tasks,
      { ...DEFAULT_BOARD_FILTERS, agentId: withAgent.assigneeAgentId! },
      NOW,
    );
    expect(byAgent.length).toBeGreaterThan(0);
    expect(byAgent.every((task) => task.assigneeAgentId === withAgent.assigneeAgentId)).toBe(true);
  });

  it('filtra por período sobre a última atividade (updatedAt)', () => {
    const recent = new Date('2026-07-19T10:00:00Z');
    const old = new Date('2026-06-01T10:00:00Z');
    const sample = [
      { ...tasks[0], updatedAt: recent.toISOString() },
      { ...tasks[1], updatedAt: old.toISOString() },
    ];
    expect(periodCutoff('all', NOW)).toBeNull();
    expect(filterBoardTasks(sample, { ...DEFAULT_BOARD_FILTERS, period: 'today' }, NOW)).toHaveLength(1);
    expect(filterBoardTasks(sample, { ...DEFAULT_BOARD_FILTERS, period: '7d' }, NOW)).toHaveLength(1);
    expect(filterBoardTasks(sample, { ...DEFAULT_BOARD_FILTERS, period: 'all' }, NOW)).toHaveLength(2);
  });
});

describe('board-export: CSV compatível com Excel', () => {
  const agentNames = new Map<string, string>([['agent-1', 'Lia']]);
  const summary = summarizeAttemptsByTask(attempts);

  it('resume tentativas por tarefa (contagem + último estado pelo número)', () => {
    const withAttempts = attempts[0].taskId;
    const entry = summary.get(withAttempts)!;
    const expected = attempts.filter((attempt) => attempt.taskId === withAttempts);
    expect(entry.count).toBe(expected.length);
    const last = [...expected].sort((a, b) => b.number - a.number)[0];
    expect(entry.lastState).toBe(last.state);
    expect(summary.get('sem-tentativas')).toBeUndefined();
  });

  it('gera cabeçalho fixo, separador `;`, enums brutos e flag de arquivamento', () => {
    const archivedTask = tasks.find((task) => task.archivedAt !== null)!;
    const activeTask = tasks.find((task) => task.archivedAt === null)!;
    const csv = boardTasksToCsv([activeTask, archivedTask], agentNames, summary);
    const lines = csv.split('\n');
    expect(lines[0]).toBe(
      'id;title;state;priority;assignee;createdAt;updatedAt;attempts;lastAttemptState;archived',
    );
    expect(lines).toHaveLength(3);
    expect(lines[1].endsWith(';no')).toBe(true);
    expect(lines[2].endsWith(';yes')).toBe(true);
    // Sem responsável → célula vazia; nome resolvido quando há agente.
    const withAgent = { ...activeTask, assigneeAgentId: 'agent-1' };
    const resolved = boardTasksToCsv([withAgent], agentNames, summary).split('\n')[1];
    expect(resolved.split(';')[4]).toBe('Lia');
  });

  it('escapa `;`, aspas e quebras de linha nas células', () => {
    const tricky = {
      ...tasks[0],
      title: 'Título com; ponto-e-vírgula, "aspas"\ne quebra',
    };
    const csv = boardTasksToCsv([tricky], agentNames, summary);
    expect(csv).toContain('"Título com; ponto-e-vírgula, ""aspas""\ne quebra"');
  });

  it('nome do arquivo leva a data da exportação', () => {
    expect(boardCsvFilename(new Date('2026-07-19T15:00:00Z'))).toBe('poseidon-quadro-2026-07-19.csv');
  });
});
