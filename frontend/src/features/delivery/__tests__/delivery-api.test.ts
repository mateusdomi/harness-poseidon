import { describe, expect, it } from 'vitest';

import { ApiError } from '@/api';

import { MockDeliveryApi } from '../api/delivery-api';
import { MOCK_DELIVERY_IDS } from '../api/mock-data';

const D1 = MOCK_DELIVERY_IDS.d1;

describe('MockDeliveryApi (DEL-01..10)', () => {
  it('lista o portfólio e filtra por "precisa de atenção"', async () => {
    const api = new MockDeliveryApi();
    // Contrato do backend: `view=portfolio` (todas) e `view=attention`; "all" não é aceito.
    const all = await api.listPortfolio('portfolio');
    expect(all.total).toBe(3);

    const attention = await api.listPortfolio('attention');
    expect(attention.total).toBeGreaterThan(0);
    expect(attention.deliveries.every((d) => d.attentionSignals.length > 0)).toBe(true);
  });

  it('entrega 360 traz resumo executivo e histórico de previsão', async () => {
    const api = new MockDeliveryApi();
    const overview = await api.getOverview(D1);
    expect(overview.executiveSummary.name).toBe('Portal do Cliente');
    expect(overview.planAndMilestones.forecastHistory.length).toBeGreaterThan(0);
  });

  it('previsão honesta: entrega sem evidência não inventa data', async () => {
    const api = new MockDeliveryApi();
    const noOwner = await api.getForecast(MOCK_DELIVERY_IDS.d3);
    expect(noOwner.latest?.hasSufficientEvidence).toBe(false);
    expect(noOwner.latest?.forecastDate).toBeNull();
  });

  it('recalcular previsão ANEXA ao histórico, nunca sobrescreve', async () => {
    const api = new MockDeliveryApi();
    const before = await api.getForecast(D1);
    await api.recalcForecast(D1);
    const after = await api.getForecast(D1);
    expect(after.history.length).toBe(before.history.length + 1);
  });

  it('métricas trazem DORA e próprias, com medidas e não-medidas', async () => {
    const api = new MockDeliveryApi();
    const metrics = await api.getMetrics(D1);
    expect(metrics.dora.length).toBeGreaterThan(0);
    expect(metrics.own.length).toBeGreaterThan(0);
    expect(metrics.dora.some((m) => !m.measured)).toBe(true);
  });

  it('ciclo do relatório: gerar → aprovar → enviar', async () => {
    const api = new MockDeliveryApi();
    const report = await api.generateReport(D1, { type: 'weekly_executive_status', format: 'markdown' });
    expect(report.status).toBe('draft');

    const approved = await api.approveReport(D1, report.id, {});
    expect(approved.status).toBe('approved');
    expect(approved.approvedBy).toBeTruthy();

    const receipt = await api.sendReport(D1, report.id, {
      channel: 'email',
      recipientReference: 'secret://inbox',
    });
    expect(receipt.result).toBe('delivered');
    expect(receipt.report.status).toBe('sent');
  });

  it('envio antes de aprovar é recusado (409)', async () => {
    const api = new MockDeliveryApi();
    const report = await api.generateReport(D1, { type: 'milestone_report', format: 'markdown' });
    await expect(
      api.sendReport(D1, report.id, { channel: 'email', recipientReference: 'secret://inbox' }),
    ).rejects.toBeInstanceOf(ApiError);
  });

  it('envio com destinatário literal (não opaco) é recusado (400)', async () => {
    const api = new MockDeliveryApi();
    const report = await api.generateReport(D1, { type: 'milestone_report', format: 'markdown' });
    await api.approveReport(D1, report.id, {});
    await expect(
      api.sendReport(D1, report.id, { channel: 'email', recipientReference: 'ops@empresa.com' }),
    ).rejects.toBeInstanceOf(ApiError);
  });

  it('daily: briefing, captura tipada e resumo', async () => {
    const api = new MockDeliveryApi();
    const briefing = await api.getDailyBriefing(D1);
    expect(briefing.isFirstDaily).toBe(true);

    await api.captureDaily(D1, { kind: 'decision', note: 'Definir provedor de e-mail.' });
    const summary = await api.getDailySummary(D1);
    expect(summary.totalCaptures).toBe(1);
    expect(summary.captures[0]?.kind).toBe('decision');
    expect(summary.createdPoCards).toBe(false);
  });

  it('entrega inexistente resulta em 404', async () => {
    const api = new MockDeliveryApi();
    await expect(api.getOverview('01JQZZZZZZZZZZZZZZZZZZZZZZ')).rejects.toBeInstanceOf(ApiError);
  });
});
