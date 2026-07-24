import type {
  DailyCapture,
  Delivery360,
  DeliveryForecast,
  DeliveryForecastHistory,
  DeliveryMetrics,
  DeliverySummary,
  DeliveryReport,
} from './types';

/**
 * Modelo determinístico da Central de Entregas para o modo `mock` e os
 * testes. Espelha os shapes do backend (DEL-01..10) com dados coerentes:
 * uma entrega saudável, uma em risco (data comprometida) e uma sem dono.
 * Nada aqui inventa números fora dos contratos — é só semente de UI.
 */

export interface DeliveryStore {
  summaries: DeliverySummary[];
  overviews: Map<string, Delivery360>;
  forecasts: Map<string, DeliveryForecast[]>;
  metrics: Map<string, DeliveryMetrics>;
  reports: Map<string, DeliveryReport[]>;
  captures: Map<string, DailyCapture[]>;
}

/** ULIDs fixas (26 chars Crockford) para IDs estáveis entre execuções. */
const IDS = {
  d1: '01JQDEL0000000000000000001',
  d2: '01JQDEL0000000000000000002',
  d3: '01JQDEL0000000000000000003',
  p1: '01JQPRJ0000000000000000001',
  p2: '01JQPRJ0000000000000000002',
  p3: '01JQPRJ0000000000000000003',
} as const;

const NOW = '2026-07-23T12:00:00.000Z';

function forecast(
  id: string,
  date: string | null,
  confidence: string,
  percent: number,
  enough: boolean,
  createdAt: string,
): DeliveryForecast {
  return {
    id,
    forecastDate: date,
    confidence,
    confidencePercent: percent,
    hasSufficientEvidence: enough,
    basis: [
      { signal: 'milestone_progress', detail: '6 de 9 marcos concluídos (67%).' },
      { signal: 'open_dependencies', detail: '1 dependência aberta.' },
      { signal: 'forecast_variance', detail: 'Variação média de 2 dias no histórico.' },
    ],
    createdAt,
  };
}

function healthy(): Delivery360 {
  const fc = forecast('01JQFC1000000000000000001', '2026-08-14T00:00:00.000Z', 'high', 82, true, NOW);
  return {
    deliveryId: IDS.d1,
    projectId: IDS.p1,
    executiveSummary: {
      name: 'Portal do Cliente',
      key: 'PORTAL',
      criticality: 'high',
      health: 'green',
      predictability: 'on_track',
      owner: 'Ana Ribeiro',
      milestonesTotal: 9,
      milestonesDone: 6,
      openTaskCount: 12,
      blockedTaskCount: 0,
      committedDate: '2026-08-15T00:00:00.000Z',
      forecastDate: '2026-08-14T00:00:00.000Z',
      lastActivityAt: '2026-07-23T09:30:00.000Z',
      attentionSignalCount: 0,
    },
    planAndMilestones: {
      milestonesTotal: 9,
      milestonesDone: 6,
      committedDate: '2026-08-15T00:00:00.000Z',
      forecast: fc,
      forecastHistory: [
        forecast('01JQFC1000000000000000000', '2026-08-16T00:00:00.000Z', 'medium', 61, true, '2026-07-16T12:00:00.000Z'),
        fc,
      ],
    },
    technicalHealth: {
      indicators: [
        { key: 'task_completion', label: 'Conclusão de tarefas', status: 'good', value: '67%', detail: '6/9 marcos.' },
        { key: 'blocked_tasks', label: 'Tarefas bloqueadas', status: 'good', value: '0', detail: 'Nenhum bloqueio ativo.' },
        { key: 'attempt_success_rate', label: 'Sucesso das tentativas', status: 'good', value: '94%', detail: '47/50 aprovadas.' },
        { key: 'documentation_coverage', label: 'Cobertura de docs', status: 'watch', value: '4/6', detail: 'Runbook pendente.' },
        { key: 'open_dependencies', label: 'Dependências abertas', status: 'good', value: '1', detail: 'Gateway de pagamento.' },
      ],
    },
    risksAndDependencies: { risks: [], openDependencies: 1, blockedTaskCount: 0 },
    decisions: {
      total: 3,
      open: 1,
      decisions: [
        { taskId: '01JQTSK0000000000000000001', state: 'approved', detail: 'Adotar autenticação por SSO.', resolved: true },
        { taskId: '01JQTSK0000000000000000002', state: 'open', detail: 'Definir provedor de e-mail transacional.', resolved: false },
      ],
    },
    documentation: {
      expected: 6,
      present: 4,
      checklist: [
        { kind: 'design', label: 'Documento de arquitetura', present: true, state: 'approved' },
        { kind: 'runbook', label: 'Runbook operacional', present: false, state: null },
        { kind: 'decision', label: 'Registro de decisões', present: true, state: 'approved' },
        { kind: 'progress', label: 'Relatório de progresso', present: true, state: 'draft' },
      ],
    },
    valueAndMetrics: {
      totalCostUsd: 42.18,
      totalTasks: 34,
      features: [
        {
          featureId: 'FEAT-LOGIN',
          taskCount: 12,
          attemptCount: 18,
          successCount: 16,
          failureCount: 2,
          totalCostUsd: 18.4,
          totalTokensInput: 1_240_000,
          totalTokensOutput: 320_000,
          totalDurationMs: 5_400_000,
        },
        {
          featureId: 'FEAT-DASHBOARD',
          taskCount: 22,
          attemptCount: 32,
          successCount: 31,
          failureCount: 1,
          totalCostUsd: 23.78,
          totalTokensInput: 2_010_000,
          totalTokensOutput: 540_000,
          totalDurationMs: 8_900_000,
        },
      ],
    },
  };
}

function atRisk(): Delivery360 {
  const fc = forecast('01JQFC2000000000000000001', '2026-09-02T00:00:00.000Z', 'low', 34, true, NOW);
  return {
    deliveryId: IDS.d2,
    projectId: IDS.p2,
    executiveSummary: {
      name: 'Motor de Cobrança',
      key: 'BILLING',
      criticality: 'critical',
      health: 'red',
      predictability: 'off_track',
      owner: 'Carlos Mendes',
      milestonesTotal: 8,
      milestonesDone: 3,
      openTaskCount: 21,
      blockedTaskCount: 4,
      committedDate: '2026-08-20T00:00:00.000Z',
      forecastDate: '2026-09-02T00:00:00.000Z',
      lastActivityAt: '2026-07-22T18:00:00.000Z',
      attentionSignalCount: 3,
    },
    planAndMilestones: {
      milestonesTotal: 8,
      milestonesDone: 3,
      committedDate: '2026-08-20T00:00:00.000Z',
      forecast: fc,
      forecastHistory: [fc],
    },
    technicalHealth: {
      indicators: [
        { key: 'task_completion', label: 'Conclusão de tarefas', status: 'bad', value: '37%', detail: '3/8 marcos.' },
        { key: 'blocked_tasks', label: 'Tarefas bloqueadas', status: 'bad', value: '4', detail: 'Aguardando acesso ao banco.' },
        { key: 'attempt_success_rate', label: 'Sucesso das tentativas', status: 'watch', value: '78%', detail: '39/50 aprovadas.' },
        { key: 'documentation_coverage', label: 'Cobertura de docs', status: 'bad', value: '2/6', detail: 'Arquitetura ausente.' },
      ],
    },
    risksAndDependencies: {
      risks: [
        { code: 'committed_date_risk', severity: 'critical', detail: 'Previsão 13 dias após a data comprometida.' },
        { code: 'pending_db_access', severity: 'critical', detail: '4 tarefas aguardando acesso ao banco de produção.' },
        { code: 'architectural_decision', severity: 'warning', detail: 'Decisão pendente sobre particionamento.' },
      ],
      openDependencies: 3,
      blockedTaskCount: 4,
    },
    decisions: {
      total: 2,
      open: 2,
      decisions: [
        { taskId: '01JQTSK0000000000000000010', state: 'open', detail: 'Estratégia de particionamento do banco.', resolved: false },
        { taskId: '01JQTSK0000000000000000011', state: 'open', detail: 'Retry x idempotência nos webhooks.', resolved: false },
      ],
    },
    documentation: {
      expected: 6,
      present: 2,
      checklist: [
        { kind: 'design', label: 'Documento de arquitetura', present: false, state: null },
        { kind: 'runbook', label: 'Runbook operacional', present: false, state: null },
        { kind: 'decision', label: 'Registro de decisões', present: true, state: 'draft' },
        { kind: 'progress', label: 'Relatório de progresso', present: true, state: 'draft' },
      ],
    },
    valueAndMetrics: {
      totalCostUsd: 88.5,
      totalTasks: 41,
      features: [
        {
          featureId: 'FEAT-WEBHOOKS',
          taskCount: 25,
          attemptCount: 60,
          successCount: 44,
          failureCount: 16,
          totalCostUsd: 62.1,
          totalTokensInput: 3_800_000,
          totalTokensOutput: 910_000,
          totalDurationMs: 14_200_000,
        },
      ],
    },
  };
}

function noOwner(): Delivery360 {
  const fc = forecast('01JQFC3000000000000000001', null, 'low', 12, false, NOW);
  return {
    deliveryId: IDS.d3,
    projectId: IDS.p3,
    executiveSummary: {
      name: 'App de Notificações',
      key: 'NOTIFY',
      criticality: 'medium',
      health: 'yellow',
      predictability: 'unknown',
      owner: null,
      milestonesTotal: 5,
      milestonesDone: 1,
      openTaskCount: 8,
      blockedTaskCount: 1,
      committedDate: null,
      forecastDate: null,
      lastActivityAt: '2026-07-10T14:00:00.000Z',
      attentionSignalCount: 2,
    },
    planAndMilestones: {
      milestonesTotal: 5,
      milestonesDone: 1,
      committedDate: null,
      forecast: fc,
      forecastHistory: [fc],
    },
    technicalHealth: {
      indicators: [
        { key: 'task_completion', label: 'Conclusão de tarefas', status: 'watch', value: '20%', detail: '1/5 marcos.' },
        { key: 'blocked_tasks', label: 'Tarefas bloqueadas', status: 'watch', value: '1', detail: 'Dependência de infra.' },
      ],
    },
    risksAndDependencies: {
      risks: [
        { code: 'no_owner', severity: 'warning', detail: 'Entrega sem responsável definido.' },
        { code: 'no_recent_update', severity: 'warning', detail: 'Sem atualização há 13 dias.' },
      ],
      openDependencies: 1,
      blockedTaskCount: 1,
    },
    decisions: { total: 0, open: 0, decisions: [] },
    documentation: {
      expected: 6,
      present: 1,
      checklist: [{ kind: 'design', label: 'Documento de arquitetura', present: true, state: 'draft' }],
    },
    valueAndMetrics: { totalCostUsd: 6.2, totalTasks: 9, features: [] },
  };
}

function summaryOf(o: Delivery360): DeliverySummary {
  const e = o.executiveSummary;
  return {
    deliveryId: o.deliveryId,
    projectId: o.projectId,
    name: e.name,
    key: e.key,
    health: e.health,
    predictability: e.predictability,
    owner: e.owner,
    committedDate: e.committedDate,
    forecastDate: e.forecastDate,
    forecastConfidence: o.planAndMilestones.forecast.confidence,
    milestonesTotal: e.milestonesTotal,
    milestonesDone: e.milestonesDone,
    openTaskCount: e.openTaskCount,
    blockedTaskCount: e.blockedTaskCount,
    lastActivityAt: e.lastActivityAt,
    attentionSignals: o.risksAndDependencies.risks,
  };
}

function metricsOf(o: Delivery360): DeliveryMetrics {
  return {
    deliveryId: o.deliveryId,
    generatedAt: NOW,
    dora: [
      { key: 'change_lead_time', label: 'Lead time de mudança', category: 'dora', measured: true, value: '2.4', unit: 'dias', basis: 'Média entre commit e produção.' },
      { key: 'deployment_frequency', label: 'Frequência de deploy', category: 'dora', measured: true, value: '3.1', unit: '/semana', basis: 'Deploys nas últimas 4 semanas.' },
      { key: 'change_fail_rate', label: 'Taxa de falha de mudança', category: 'dora', measured: false, value: null, unit: null, basis: 'Sem sinal de deploy falho registrado ainda.' },
      { key: 'failed_deploy_recovery', label: 'Recuperação de deploy falho', category: 'dora', measured: false, value: null, unit: null, basis: 'Sem incidentes de deploy no período.' },
    ],
    own: [
      { key: 'forecast_accuracy', label: 'Precisão da previsão', category: 'own', measured: true, value: '88', unit: '%', basis: 'Desvio médio de 2 dias no histórico.' },
      { key: 'doc_coverage', label: 'Cobertura de documentação', category: 'own', measured: true, value: `${Math.round((o.documentation.present / o.documentation.expected) * 100)}`, unit: '%', basis: `${o.documentation.present}/${o.documentation.expected} documentos presentes.` },
      { key: 'scope_changes', label: 'Mudanças de escopo', category: 'own', measured: true, value: '2', unit: '', basis: 'Marcações de escopo na daily.' },
      { key: 'waiting_access_time', label: 'Tempo aguardando acesso', category: 'own', measured: o.executiveSummary.blockedTaskCount > 0, value: o.executiveSummary.blockedTaskCount > 0 ? '3.5' : null, unit: 'dias', basis: 'Tarefas bloqueadas por acesso.' },
    ],
  };
}

/** Constrói uma cópia nova do store a cada chamada — estado isolado por instância. */
export function buildDeliveryStore(): DeliveryStore {
  const overviewsArr = [healthy(), atRisk(), noOwner()];
  const overviews = new Map<string, Delivery360>();
  const forecasts = new Map<string, DeliveryForecast[]>();
  const metrics = new Map<string, DeliveryMetrics>();
  const reports = new Map<string, DeliveryReport[]>();
  const captures = new Map<string, DailyCapture[]>();

  for (const o of overviewsArr) {
    overviews.set(o.deliveryId, o);
    forecasts.set(o.deliveryId, o.planAndMilestones.forecastHistory.slice());
    metrics.set(o.deliveryId, metricsOf(o));
    reports.set(o.deliveryId, []);
    captures.set(o.deliveryId, []);
  }

  return {
    summaries: overviewsArr.map(summaryOf),
    overviews,
    forecasts,
    metrics,
    reports,
    captures,
  };
}

export function forecastHistoryOf(store: DeliveryStore, deliveryId: string): DeliveryForecastHistory {
  const history = store.forecasts.get(deliveryId) ?? [];
  return {
    deliveryId,
    latest: history.length > 0 ? history[history.length - 1] : null,
    history: history.slice(),
  };
}

export const MOCK_DELIVERY_IDS = IDS;
