import { ApiError, problemDetailsSchema } from '@/api';

import {
  dailyBriefingSchema,
  dailyCaptureSchema,
  dailySummarySchema,
  delivery360Schema,
  deliveryForecastHistorySchema,
  deliveryForecastSchema,
  deliveryMetricsSchema,
  deliveryPlanningSchema,
  deliveryPortfolioSchema,
  deliveryReportListSchema,
  deliveryReportSchema,
  reportSendReceiptSchema,
  type ApproveReportInput,
  type DailyBriefing,
  type DailyCapture,
  type DailyCaptureInput,
  type DailySummary,
  type Delivery360,
  type DeliveryForecast,
  type DeliveryForecastHistory,
  type DeliveryMetrics,
  type DeliveryPlanning,
  type DeliveryPlanningInput,
  type DeliveryPortfolio,
  type DeliveryReport,
  type DeliveryReportList,
  type GenerateReportInput,
  type ReportSendReceipt,
  type SendReportInput,
} from './types';
import { buildDeliveryStore, forecastHistoryOf, type DeliveryStore } from './mock-data';

/**
 * Cliente da Central de Entregas consumido pela UI (DEL-01..10). Mesma
 * filosofia do resto do app: a UI só enxerga esta interface; trocar
 * mock ↔ http (via `VITE_API_MODE`) não exige tocar em componente algum.
 */
export interface DeliveryApi {
  listPortfolio(view?: string): Promise<DeliveryPortfolio>;
  getOverview(deliveryId: string): Promise<Delivery360>;
  configurePlanning(deliveryId: string, input: DeliveryPlanningInput): Promise<DeliveryPlanning>;
  getForecast(deliveryId: string): Promise<DeliveryForecastHistory>;
  recalcForecast(deliveryId: string): Promise<DeliveryForecast>;
  getMetrics(deliveryId: string): Promise<DeliveryMetrics>;
  listReports(deliveryId: string): Promise<DeliveryReportList>;
  getReport(deliveryId: string, reportId: string): Promise<DeliveryReport>;
  generateReport(deliveryId: string, input: GenerateReportInput): Promise<DeliveryReport>;
  approveReport(deliveryId: string, reportId: string, input: ApproveReportInput): Promise<DeliveryReport>;
  sendReport(deliveryId: string, reportId: string, input: SendReportInput): Promise<ReportSendReceipt>;
  getDailyBriefing(deliveryId: string): Promise<DailyBriefing>;
  captureDaily(deliveryId: string, input: DailyCaptureInput): Promise<DailyCapture>;
  getDailySummary(deliveryId: string): Promise<DailySummary>;
}

/* ------------------------------------------------------------------ */
/* HTTP                                                                */
/* ------------------------------------------------------------------ */

export interface HttpDeliveryApiOptions {
  baseUrl: string;
  fetchFn?: typeof fetch;
}

/** Implementação real: fetch em `/api/v1/deliveries/*`, erros RFC 7807. */
export class HttpDeliveryApi implements DeliveryApi {
  readonly #base: string;
  readonly #fetch: typeof fetch;

  constructor(options: HttpDeliveryApiOptions) {
    this.#base = `${options.baseUrl.replace(/\/$/, '')}/api/v1/deliveries`;
    this.#fetch = options.fetchFn ?? ((input, init) => fetch(input, init));
  }

  async #request(method: string, path: string, body?: unknown): Promise<unknown> {
    const response = await this.#fetch(`${this.#base}${path}`, {
      method,
      credentials: 'include',
      headers: body !== undefined ? { 'content-type': 'application/json' } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      let problem;
      try {
        problem = problemDetailsSchema.parse(await response.json());
      } catch {
        throw ApiError.of(response.status, response.statusText || 'Erro');
      }
      throw new ApiError(problem);
    }
    if (response.status === 204) return undefined;
    return response.json();
  }

  async listPortfolio(view?: string): Promise<DeliveryPortfolio> {
    const query = view ? `?view=${encodeURIComponent(view)}` : '';
    return deliveryPortfolioSchema.parse(await this.#request('GET', query));
  }

  async getOverview(deliveryId: string): Promise<Delivery360> {
    return delivery360Schema.parse(await this.#request('GET', `/${deliveryId}/overview`));
  }

  async configurePlanning(
    deliveryId: string,
    input: DeliveryPlanningInput,
  ): Promise<DeliveryPlanning> {
    return deliveryPlanningSchema.parse(
      await this.#request('POST', `/${deliveryId}/planning`, input),
    );
  }

  async getForecast(deliveryId: string): Promise<DeliveryForecastHistory> {
    return deliveryForecastHistorySchema.parse(await this.#request('GET', `/${deliveryId}/forecast`));
  }

  async recalcForecast(deliveryId: string): Promise<DeliveryForecast> {
    return deliveryForecastSchema.parse(await this.#request('POST', `/${deliveryId}/forecast`, {}));
  }

  async getMetrics(deliveryId: string): Promise<DeliveryMetrics> {
    return deliveryMetricsSchema.parse(await this.#request('GET', `/${deliveryId}/metrics`));
  }

  async listReports(deliveryId: string): Promise<DeliveryReportList> {
    return deliveryReportListSchema.parse(await this.#request('GET', `/${deliveryId}/reports`));
  }

  async getReport(deliveryId: string, reportId: string): Promise<DeliveryReport> {
    return deliveryReportSchema.parse(await this.#request('GET', `/${deliveryId}/reports/${reportId}`));
  }

  async generateReport(deliveryId: string, input: GenerateReportInput): Promise<DeliveryReport> {
    return deliveryReportSchema.parse(await this.#request('POST', `/${deliveryId}/reports`, input));
  }

  async approveReport(
    deliveryId: string,
    reportId: string,
    input: ApproveReportInput,
  ): Promise<DeliveryReport> {
    return deliveryReportSchema.parse(
      await this.#request('POST', `/${deliveryId}/reports/${reportId}/approve`, input),
    );
  }

  async sendReport(
    deliveryId: string,
    reportId: string,
    input: SendReportInput,
  ): Promise<ReportSendReceipt> {
    return reportSendReceiptSchema.parse(
      await this.#request('POST', `/${deliveryId}/reports/${reportId}/send`, input),
    );
  }

  async getDailyBriefing(deliveryId: string): Promise<DailyBriefing> {
    return dailyBriefingSchema.parse(await this.#request('GET', `/${deliveryId}/daily/briefing`));
  }

  async captureDaily(deliveryId: string, input: DailyCaptureInput): Promise<DailyCapture> {
    return dailyCaptureSchema.parse(
      await this.#request('POST', `/${deliveryId}/daily/captures`, input),
    );
  }

  async getDailySummary(deliveryId: string): Promise<DailySummary> {
    return dailySummarySchema.parse(await this.#request('GET', `/${deliveryId}/daily/summary`));
  }
}

/* ------------------------------------------------------------------ */
/* MOCK                                                                */
/* ------------------------------------------------------------------ */

/**
 * Implementação em memória, determinística e stateful — semeada com um
 * portfólio rico (saudável / em risco / sem dono). Usada nos testes e no
 * modo `mock`: gerar/aprovar/enviar relatórios e capturas de daily mutam
 * o store, refletindo o ciclo de vida real (rascunho → aprovado → enviado).
 */
export class MockDeliveryApi implements DeliveryApi {
  readonly #store: DeliveryStore;
  #seq = 0;

  constructor(store: DeliveryStore = buildDeliveryStore()) {
    this.#store = store;
  }

  #nextId(prefix: string): string {
    this.#seq += 1;
    return `${prefix}${this.#seq.toString().padStart(24, '0')}`;
  }

  #overview(deliveryId: string): Delivery360 {
    const o = this.#store.overviews.get(deliveryId);
    if (!o) throw ApiError.of(404, 'Entrega não encontrada');
    return o;
  }

  async listPortfolio(view = 'all'): Promise<DeliveryPortfolio> {
    let deliveries = this.#store.summaries.slice();
    if (view === 'attention') {
      deliveries = deliveries.filter((d) => d.attentionSignals.length > 0);
    }
    return { view, total: deliveries.length, deliveries };
  }

  async getOverview(deliveryId: string): Promise<Delivery360> {
    return structuredClone(this.#overview(deliveryId));
  }

  async configurePlanning(
    deliveryId: string,
    input: DeliveryPlanningInput,
  ): Promise<DeliveryPlanning> {
    const overview = this.#overview(deliveryId);
    const summary = this.#store.summaries.find((item) => item.deliveryId === deliveryId);
    const now = new Date().toISOString();
    const ownerName = input.ownerAgentId;
    if (summary) {
      summary.owner = input.ownerAgentId;
      summary.committedDate = input.committedDate;
      summary.lastActivityAt = now;
    }
    overview.executiveSummary.owner = input.ownerAgentId;
    overview.executiveSummary.committedDate = input.committedDate;
    overview.executiveSummary.lastActivityAt = now;
    overview.planAndMilestones.committedDate = input.committedDate;
    return {
      deliveryId,
      ownerAgentId: input.ownerAgentId,
      ownerName,
      committedDate: input.committedDate,
      updatedTaskCount: overview.executiveSummary.openTaskCount,
      updatedAt: now,
    };
  }

  async getForecast(deliveryId: string): Promise<DeliveryForecastHistory> {
    this.#overview(deliveryId);
    return structuredClone(forecastHistoryOf(this.#store, deliveryId));
  }

  async recalcForecast(deliveryId: string): Promise<DeliveryForecast> {
    const o = this.#overview(deliveryId);
    const base = o.planAndMilestones.forecast;
    const next: DeliveryForecast = {
      ...structuredClone(base),
      id: this.#nextId('01JQFCX'),
      createdAt: new Date().toISOString(),
    };
    const history = this.#store.forecasts.get(deliveryId) ?? [];
    history.push(next);
    this.#store.forecasts.set(deliveryId, history);
    return structuredClone(next);
  }

  async getMetrics(deliveryId: string): Promise<DeliveryMetrics> {
    this.#overview(deliveryId);
    return structuredClone(this.#store.metrics.get(deliveryId)!);
  }

  async listReports(deliveryId: string): Promise<DeliveryReportList> {
    this.#overview(deliveryId);
    const reports = this.#store.reports.get(deliveryId) ?? [];
    return {
      deliveryId,
      total: reports.length,
      reports: reports.map((r) => ({
        id: r.id,
        deliveryId: r.deliveryId,
        type: r.type,
        format: r.format,
        status: r.status,
        audience: r.audience,
        classification: r.classification,
        version: r.version,
        available: r.available,
        approvedBy: r.approvedBy,
        approvedAt: r.approvedAt,
        sentAt: r.sentAt,
        createdAt: r.createdAt,
      })),
    };
  }

  async getReport(deliveryId: string, reportId: string): Promise<DeliveryReport> {
    const report = (this.#store.reports.get(deliveryId) ?? []).find((r) => r.id === reportId);
    if (!report) throw ApiError.of(404, 'Relatório não encontrado');
    return structuredClone(report);
  }

  async generateReport(deliveryId: string, input: GenerateReportInput): Promise<DeliveryReport> {
    const o = this.#overview(deliveryId);
    const format = input.format || 'markdown';
    const renderable = ['markdown', 'html', 'csv', 'json'].includes(format);
    const now = new Date().toISOString();
    const audience = input.audience || 'coordination';
    const classification = input.classification || 'internal';
    const doc = {
      type: input.type,
      title: `${input.type} — ${o.executiveSummary.name}`,
      deliveryId,
      projectKey: o.executiveSummary.key,
      audience,
      classification,
      generatedAt: now,
      sections: [
        {
          key: 'executive_summary',
          title: 'Resumo executivo',
          fields: [
            { label: 'Entrega', value: o.executiveSummary.name },
            { label: 'Saúde', value: o.executiveSummary.health },
            { label: 'Previsibilidade', value: o.executiveSummary.predictability },
            {
              label: 'Marcos',
              value: `${o.executiveSummary.milestonesDone}/${o.executiveSummary.milestonesTotal}`,
            },
          ],
          table: null,
        },
        {
          key: 'forecast',
          title: 'Previsão honesta',
          fields: [
            { label: 'Data prevista', value: o.planAndMilestones.forecast.forecastDate ?? '—' },
            { label: 'Confiança', value: o.planAndMilestones.forecast.confidence },
          ],
          table: null,
        },
      ],
    };
    const report: DeliveryReport = {
      id: this.#nextId('01JQRPT'),
      deliveryId,
      projectId: o.projectId,
      type: input.type,
      format,
      status: 'draft',
      audience,
      classification,
      version: 1,
      contentType: renderable ? 'text/markdown' : 'application/octet-stream',
      available: renderable,
      content: renderable ? `# ${doc.title}\n\nRelatório gerado (mock).` : null,
      reason: renderable ? null : 'format_not_available',
      approvedBy: null,
      approvedAt: null,
      sentAt: null,
      createdAt: now,
      document: doc,
    };
    const list = this.#store.reports.get(deliveryId) ?? [];
    list.push(report);
    this.#store.reports.set(deliveryId, list);
    return structuredClone(report);
  }

  #find(deliveryId: string, reportId: string): DeliveryReport {
    const report = (this.#store.reports.get(deliveryId) ?? []).find((r) => r.id === reportId);
    if (!report) throw ApiError.of(404, 'Relatório não encontrado');
    return report;
  }

  async approveReport(
    deliveryId: string,
    reportId: string,
    input: ApproveReportInput,
  ): Promise<DeliveryReport> {
    const report = this.#find(deliveryId, reportId);
    if (report.status === 'sent') {
      throw ApiError.of(409, 'Relatório já enviado');
    }
    report.status = 'approved';
    report.approvedBy = input.approvedBy?.trim() || 'Você';
    report.approvedAt = new Date().toISOString();
    return structuredClone(report);
  }

  async sendReport(
    deliveryId: string,
    reportId: string,
    input: SendReportInput,
  ): Promise<ReportSendReceipt> {
    const report = this.#find(deliveryId, reportId);
    if (report.status !== 'approved') {
      throw ApiError.of(409, 'O relatório precisa ser aprovado antes do envio');
    }
    if (!/^(env|secret|keychain):\/\//.test(input.recipientReference)) {
      throw ApiError.of(400, 'O destinatário deve ser uma referência opaca (env://, secret:// ou keychain://)');
    }
    const now = new Date().toISOString();
    report.status = 'sent';
    report.sentAt = now;
    return {
      reportId,
      version: report.version,
      channel: input.channel,
      sentBy: report.approvedBy ?? 'Você',
      sentAt: now,
      result: 'delivered',
      report: structuredClone(report),
    };
  }

  async getDailyBriefing(deliveryId: string): Promise<DailyBriefing> {
    const o = this.#overview(deliveryId);
    const captures = this.#store.captures.get(deliveryId) ?? [];
    return {
      deliveryId,
      generatedAt: new Date().toISOString(),
      isFirstDaily: captures.length === 0,
      lastDailyAt: captures.length > 0 ? captures[captures.length - 1]!.createdAt : null,
      snapshot: {
        health: o.executiveSummary.health,
        predictability: o.executiveSummary.predictability,
        openTaskCount: o.executiveSummary.openTaskCount,
        blockedTaskCount: o.executiveSummary.blockedTaskCount,
        milestonesTotal: o.executiveSummary.milestonesTotal,
        milestonesDone: o.executiveSummary.milestonesDone,
        forecast: structuredClone(o.planAndMilestones.forecast),
      },
      changesSinceLast: o.risksAndDependencies.blockedTaskCount > 0
        ? [{ code: 'blocked_tasks', detail: `${o.risksAndDependencies.blockedTaskCount} tarefa(s) bloqueada(s) desde a última daily.` }]
        : [{ code: 'no_changes', detail: 'Sem mudanças relevantes desde a última daily.' }],
      itemsNeedingAttention: structuredClone(o.risksAndDependencies.risks),
      recommendedQuestions: o.risksAndDependencies.risks.map((r) => ({
        topic: r.code,
        question: `Como destravar: ${r.detail}`,
      })),
    };
  }

  async captureDaily(deliveryId: string, input: DailyCaptureInput): Promise<DailyCapture> {
    this.#overview(deliveryId);
    const capture: DailyCapture = {
      id: this.#nextId('01JQCAP'),
      deliveryId,
      kind: input.kind,
      note: input.note,
      capturedBy: input.capturedBy?.trim() || 'Você',
      createdAt: new Date().toISOString(),
    };
    const list = this.#store.captures.get(deliveryId) ?? [];
    list.push(capture);
    this.#store.captures.set(deliveryId, list);
    return structuredClone(capture);
  }

  async getDailySummary(deliveryId: string): Promise<DailySummary> {
    const o = this.#overview(deliveryId);
    const captures = this.#store.captures.get(deliveryId) ?? [];
    const byKind = new Map<string, number>();
    for (const c of captures) byKind.set(c.kind, (byKind.get(c.kind) ?? 0) + 1);
    return {
      deliveryId,
      generatedAt: new Date().toISOString(),
      sessionSince: captures.length > 0 ? captures[0]!.createdAt : null,
      totalCaptures: captures.length,
      byKind: [...byKind.entries()].map(([kind, count]) => ({ kind, count })),
      captures: structuredClone(captures),
      snapshot: {
        health: o.executiveSummary.health,
        predictability: o.executiveSummary.predictability,
        openTaskCount: o.executiveSummary.openTaskCount,
        blockedTaskCount: o.executiveSummary.blockedTaskCount,
        milestonesTotal: o.executiveSummary.milestonesTotal,
        milestonesDone: o.executiveSummary.milestonesDone,
        forecast: structuredClone(o.planAndMilestones.forecast),
      },
      createdPoCards: false,
    };
  }
}

/* ------------------------------------------------------------------ */
/* FACTORY                                                             */
/* ------------------------------------------------------------------ */

export function createDeliveryApi(
  mode: string = import.meta.env.VITE_API_MODE ?? 'mock',
): DeliveryApi {
  if (mode === 'http') {
    const baseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5001';
    return new HttpDeliveryApi({ baseUrl });
  }
  return new MockDeliveryApi();
}
