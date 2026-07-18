import type { AuditEvent, Ulid } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  correlateAuditEvent,
  EMPTY_AUDIT_FILTERS,
  filterAuditEvents,
  matchesAuditFilters,
  matchesAuditSearch,
  resolveActorName,
  resolveEventProjectId,
  resolveTargetName,
  sortAuditEventsDesc,
  type AuditCatalog,
} from '@/features/governance/lib/audit-derive';
import { auditEventsToCsv, auditEventsToJson, AUDIT_CSV_HEADER } from '@/features/governance/lib/audit-export';

const data = createTestBundle().fixtures.data;

const catalog: AuditCatalog = {
  projects: data.projects,
  tasks: data.tasks,
  attempts: data.attempts,
  approvals: data.approvals,
  documents: data.documents,
  demands: data.demands,
  solicitations: data.solicitations,
  workflows: data.workflows,
  profiles: data.profiles,
  agents: data.agents,
  models: data.models,
  tools: data.tools,
};

let sequence = 0;
/** Evento sintético mínimo (ids/datas únicas por chamada). */
function makeEvent(overrides: Partial<AuditEvent> = {}): AuditEvent {
  sequence += 1;
  return {
    id: `01TESTEVENT0000000000000${String(sequence).padStart(3, '0')}` as Ulid,
    actorKind: 'system',
    actorId: null,
    action: 'test.action',
    targetType: 'license',
    targetId: null,
    detail: null,
    occurredAt: `2026-07-${String(Math.min(sequence, 28)).padStart(2, '0')}T12:00:00Z`,
    ...overrides,
  };
}

const fixtureEvents = data['audit-events'];

describe('sortAuditEventsDesc', () => {
  it('ordena por occurredAt desc sem mutar a entrada', () => {
    const oldest = makeEvent({ occurredAt: '2026-07-01T10:00:00Z' });
    const newest = makeEvent({ occurredAt: '2026-07-03T10:00:00Z' });
    const middle = makeEvent({ occurredAt: '2026-07-02T10:00:00Z' });
    const input = [oldest, newest, middle];
    const sorted = sortAuditEventsDesc(input);
    expect(sorted.map((event) => event.id)).toEqual([newest.id, middle.id, oldest.id]);
    expect(input[0]).toBe(oldest); // entrada intacta
  });
});

describe('matchesAuditSearch', () => {
  const event = makeEvent({ action: 'approval.resolved', detail: 'PRD aprovado sem ressalvas.' });

  it('casa action e detail ignorando caixa e espaços das pontas', () => {
    expect(matchesAuditSearch(event, 'APPROVAL')).toBe(true);
    expect(matchesAuditSearch(event, '  ressalvas ')).toBe(true);
    expect(matchesAuditSearch(event, 'budget')).toBe(false);
  });

  it('busca vazia casa tudo, inclusive sem detail', () => {
    expect(matchesAuditSearch(event, '')).toBe(true);
    expect(matchesAuditSearch(makeEvent({ detail: null }), '   ')).toBe(true);
  });
});

describe('resolução de ator e alvo', () => {
  it('resolve nome do ator (perfil/agente) e null para ator sem id', () => {
    const [workflowEvent, , , , agentEvent, systemEvent] = fixtureEvents;
    expect(resolveActorName(workflowEvent, catalog)).toBe('Mateus');
    expect(resolveActorName(agentEvent, catalog)).toBe(data.agents.find((a) => a.id === agentEvent.actorId)?.name);
    expect(resolveActorName(systemEvent, catalog)).toBeNull();
  });

  it('resolve nome do alvo conforme targetType; null quando não correlacionável', () => {
    const [workflowEvent, approvalEvent, , taskEvent, , licenseEvent] = fixtureEvents;
    // Workflow não tem nome próprio → nome do projeto vinculado.
    expect(resolveTargetName(workflowEvent, catalog)).toBe('Poseidon Frontend');
    expect(resolveTargetName(approvalEvent, catalog)).toBe('Aprovar PRD do console');
    expect(resolveTargetName(taskEvent, catalog)).toBe(data.tasks[10].title);
    // Evento de licença sem targetId não é correlacionável.
    expect(resolveTargetName(licenseEvent, catalog)).toBeNull();
  });

  it('correlaciona attempt com a tarefa dona', () => {
    const attempt = data.attempts[0];
    const event = makeEvent({ targetType: 'attempt', targetId: attempt.id });
    const correlation = correlateAuditEvent(event, catalog);
    expect(correlation.attempt?.id).toBe(attempt.id);
    expect(correlation.task?.id).toBe(attempt.taskId);
    expect(correlation.approval).toBeNull();
  });

  it('correlaciona approval e task pelo alvo', () => {
    const [, approvalEvent, , taskEvent] = fixtureEvents;
    expect(correlateAuditEvent(approvalEvent, catalog).approval?.title).toBe('Aprovar PRD do console');
    expect(correlateAuditEvent(taskEvent, catalog).task?.id).toBe(data.tasks[10].id);
  });
});

describe('resolveEventProjectId', () => {
  it('correlaciona via targetId (workflow, approval, task, agent, solicitation, demand)', () => {
    const poseidon = data.projects.find((project) => project.name === 'Poseidon Frontend')!;
    const [workflowEvent, approvalEvent, , taskEvent, agentEvent, , solicitationEvent, demandEvent] =
      fixtureEvents;
    for (const event of [
      workflowEvent,
      approvalEvent,
      taskEvent,
      agentEvent,
      solicitationEvent,
      demandEvent,
    ]) {
      expect(resolveEventProjectId(event, catalog)).toBe(poseidon.id);
    }
  });

  it('retorna null para evento sem alvo correlacionável (licença)', () => {
    const [, , , , , licenseEvent] = fixtureEvents;
    expect(resolveEventProjectId(licenseEvent, catalog)).toBeNull();
  });

  it('cai no projectId do agente ator quando o alvo não resolve', () => {
    const agent = data.agents.find((item) => item.projectId !== null)!;
    const event = makeEvent({ actorKind: 'agent', actorId: agent.id });
    expect(resolveEventProjectId(event, catalog)).toBe(agent.projectId);
  });
});

describe('matchesAuditFilters', () => {
  const filters = (partial: Partial<typeof EMPTY_AUDIT_FILTERS>) => ({
    ...EMPTY_AUDIT_FILTERS,
    ...partial,
  });

  it('filtra por tipo de ator e por ator concreto', () => {
    const [, , , , , systemEvent] = fixtureEvents;
    const onlySystem = filterAuditEvents(fixtureEvents, filters({ actorKind: 'system' }), catalog);
    expect(onlySystem).toHaveLength(1);
    expect(onlySystem[0].id).toBe(systemEvent.id);

    const profile = data.profiles[0];
    const mine = filterAuditEvents(fixtureEvents, filters({ actorId: profile.id }), catalog);
    expect(mine.every((event) => event.actorId === profile.id)).toBe(true);
    expect(mine.length).toBeGreaterThan(0);
  });

  it('filtro de projeto exclui evento não correlacionável (licença)', () => {
    const poseidon = data.projects.find((project) => project.name === 'Poseidon Frontend')!;
    const filtered = filterAuditEvents(fixtureEvents, filters({ projectId: poseidon.id }), catalog);
    // Todos menos o evento de licença (sem targetId).
    expect(filtered).toHaveLength(fixtureEvents.length - 1);
    expect(filtered.some((event) => event.targetType === 'license')).toBe(false);
  });

  it('filtra por tarefa: alvo direto, attempt ou aprovação vinculada', () => {
    const task = data.tasks[10];
    const direct = makeEvent({ targetType: 'task', targetId: task.id });
    const attempt = data.attempts[0];
    const viaAttempt = makeEvent({ targetType: 'attempt', targetId: attempt.id });
    const other = makeEvent({ targetType: 'task', targetId: data.tasks[0].id });

    const taskFilter = filters({ taskId: task.id });
    expect(matchesAuditFilters(direct, taskFilter, catalog)).toBe(true);
    expect(matchesAuditFilters(other, taskFilter, catalog)).toBe(false);

    const attemptTaskFilter = filters({ taskId: attempt.taskId });
    expect(matchesAuditFilters(viaAttempt, attemptTaskFilter, catalog)).toBe(true);

    const approval = data.approvals.find((item) => item.taskId !== null)!;
    const viaApproval = makeEvent({ targetType: 'approval', targetId: approval.id });
    expect(
      matchesAuditFilters(viaApproval, filters({ taskId: approval.taskId! }), catalog),
    ).toBe(true);
  });

  it('filtra por tentativa, modelo e ferramenta apenas quando targetType corresponde', () => {
    const attempt = data.attempts[0];
    const attemptEvent = makeEvent({ targetType: 'attempt', targetId: attempt.id });
    expect(
      matchesAuditFilters(attemptEvent, { ...EMPTY_AUDIT_FILTERS, attemptId: attempt.id }, catalog),
    ).toBe(true);
    // Mesmo id, mas targetType diferente → não casa.
    const taskEvent = makeEvent({ targetType: 'task', targetId: attempt.id as Ulid });
    expect(
      matchesAuditFilters(taskEvent, { ...EMPTY_AUDIT_FILTERS, attemptId: attempt.id }, catalog),
    ).toBe(false);

    const model = data.models[0];
    const modelEvent = makeEvent({ targetType: 'model', targetId: model.id });
    expect(
      matchesAuditFilters(modelEvent, { ...EMPTY_AUDIT_FILTERS, modelId: model.id }, catalog),
    ).toBe(true);
    expect(
      matchesAuditFilters(attemptEvent, { ...EMPTY_AUDIT_FILTERS, modelId: model.id }, catalog),
    ).toBe(false);

    const tool = data.tools[0];
    const toolEvent = makeEvent({ targetType: 'tool', targetId: tool.id });
    expect(
      matchesAuditFilters(toolEvent, { ...EMPTY_AUDIT_FILTERS, toolId: tool.id }, catalog),
    ).toBe(true);
    expect(
      matchesAuditFilters(modelEvent, { ...EMPTY_AUDIT_FILTERS, toolId: tool.id }, catalog),
    ).toBe(false);
  });

  it('filtra por período inclusivo nas duas pontas', () => {
    const event = makeEvent({ occurredAt: '2026-07-10T23:30:00Z' });
    const range = { dateFrom: '2026-07-10', dateTo: '2026-07-10' };
    expect(matchesAuditFilters(event, { ...EMPTY_AUDIT_FILTERS, ...range }, catalog)).toBe(true);
    expect(
      matchesAuditFilters(event, { ...EMPTY_AUDIT_FILTERS, dateFrom: '2026-07-11' }, catalog),
    ).toBe(false);
    expect(
      matchesAuditFilters(event, { ...EMPTY_AUDIT_FILTERS, dateTo: '2026-07-09' }, catalog),
    ).toBe(false);
  });

  it('combina filtros com AND', () => {
    const [, approvalEvent] = fixtureEvents;
    expect(
      matchesAuditFilters(
        approvalEvent,
        { ...EMPTY_AUDIT_FILTERS, actorKind: 'user', search: 'approval' },
        catalog,
      ),
    ).toBe(true);
    expect(
      matchesAuditFilters(
        approvalEvent,
        { ...EMPTY_AUDIT_FILTERS, actorKind: 'chief', search: 'approval' },
        catalog,
      ),
    ).toBe(false);
  });
});

describe('exportação (JSON/CSV)', () => {
  const secretEvent = makeEvent({
    action: 'provider.keyUpdated',
    detail: 'Chave atualizada: token=sk-super-secreto-123',
  });
  const nullDetailEvent = makeEvent({ detail: null });

  it('JSON mascara segredos do detail e preserva null', () => {
    const parsed = JSON.parse(auditEventsToJson([secretEvent, nullDetailEvent])) as AuditEvent[];
    expect(parsed[0].detail).toContain('token=****');
    expect(parsed[0].detail).not.toContain('sk-super-secreto-123');
    expect(parsed[1].detail).toBeNull();
  });

  it('CSV tem cabeçalho fixo, mascara segredos e deixa null vazio', () => {
    const csv = auditEventsToCsv([secretEvent, nullDetailEvent]);
    const lines = csv.split('\n');
    expect(lines[0]).toBe(AUDIT_CSV_HEADER.join(','));
    expect(lines[1]).toContain('token=****');
    expect(lines[1]).not.toContain('sk-super-secreto-123');
    expect(lines[2].endsWith(',')).toBe(true); // detail null → célula vazia
  });

  it('CSV escapa vírgula, aspas e quebra de linha (RFC 4180)', () => {
    const event = makeEvent({ action: 'x', detail: 'com, vírgula "aspas"\ne nova linha' });
    // A célula com quebra de linha fica cercada por aspas — não quebra o registro.
    expect(auditEventsToCsv([event])).toContain('"com, vírgula ""aspas""\ne nova linha"');
  });
});
