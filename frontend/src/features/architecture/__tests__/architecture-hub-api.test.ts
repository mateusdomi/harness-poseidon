import { describe, expect, it } from 'vitest';

import { ApiError } from '@/api';
import { MockArchitectureApi } from '../api/architecture-api';

const PROJECT = 'PROJ1';

describe('MockArchitectureApi — Architecture Hub (ARC-02/03/06/07/08/10)', () => {
  it('lista o catálogo corporativo de sistemas com criticidade e calor', async () => {
    const api = new MockArchitectureApi();
    const catalog = await api.listSystems(PROJECT);
    expect(catalog.total).toBe(catalog.systems.length);
    expect(catalog.systems.length).toBeGreaterThan(3);
    const poseidon = catalog.systems.find((s) => s.name === 'Poseidon');
    expect(poseidon?.capabilities.length).toBeGreaterThan(0);
    expect(catalog.systems.some((s) => s.heatScore >= 70)).toBe(true);
  });

  it('agrega domínios e capacidades a partir do catálogo', async () => {
    const api = new MockArchitectureApi();
    const domains = await api.getDomainMap(PROJECT);
    const capabilities = await api.getCapabilityMap(PROJECT);
    expect(domains.domains.every((d) => d.systemCount === d.systemIds.length)).toBe(true);
    // Capacidade "Cobrança" é compartilhada por mais de um sistema (redundância).
    const cobranca = capabilities.capabilities.find((c) => c.capability === 'Cobrança');
    expect(cobranca?.systemCount).toBeGreaterThan(1);
  });

  it('projeta o grafo de integração e o heatmap de risco', async () => {
    const api = new MockArchitectureApi();
    const graph = await api.getIntegrationGraph(PROJECT);
    expect(graph.edgeCount).toBe(graph.edges.length);
    const heatmap = await api.getHeatmap(PROJECT);
    expect(heatmap.systems.length).toBeGreaterThan(0);
    // Ordenado por score decrescente.
    for (let i = 1; i < heatmap.systems.length; i += 1) {
      expect(heatmap.systems[i - 1].score).toBeGreaterThanOrEqual(heatmap.systems[i].score);
    }
    expect(Object.keys(heatmap.signalTotals).length).toBeGreaterThan(0);
  });

  it('resolve o Sistema 360 com as seis facetas', async () => {
    const api = new MockArchitectureApi();
    const [first] = (await api.listSystems(PROJECT)).systems;
    const overview = await api.getSystemOverview(first.id);
    expect(overview.business.name).toBe(first.name);
    expect(overview.technology.containers.length).toBeGreaterThan(0);
    expect(overview.governance.adrCount).toBeGreaterThanOrEqual(0);
  });

  it('lança 404 para sistema inexistente', async () => {
    const api = new MockArchitectureApi();
    await expect(api.getSystemOverview('missing')).rejects.toBeInstanceOf(ApiError);
  });

  it('lista descobertas e resume por assunto (Discovery)', async () => {
    const api = new MockArchitectureApi();
    const list = await api.listDiscoveries(PROJECT);
    expect(list.discoveries.length).toBeGreaterThan(0);
    const summary = await api.getDiscoverySummary(PROJECT);
    expect(summary.subjects.every((s) => s.discoveryCount > 0)).toBe(true);
    expect(summary.subjects.some((s) => s.openCount > 0)).toBe(true);
  });

  it('gera o relatório de racionalização com classificação TIME', async () => {
    const api = new MockArchitectureApi();
    const report = await api.getInsights(PROJECT);
    expect(report.insightCount).toBe(report.insights.length);
    expect(report.insights.some((i) => i.classification === 'eliminate')).toBe(true);
    expect(Object.keys(report.byClassification).length).toBeGreaterThan(0);
  });

  it('filtra padrões por tipo (ADRs)', async () => {
    const api = new MockArchitectureApi();
    const all = await api.listPatterns(PROJECT);
    const adrs = await api.listPatterns(PROJECT, 'adr');
    expect(adrs.items.every((p) => p.kind === 'adr')).toBe(true);
    expect(adrs.items.length).toBeLessThan(all.items.length);
    const one = await api.getPattern(all.items[0].id);
    expect(one.id).toBe(all.items[0].id);
  });

  it('compara baseline planejado × as-built (conformidade e drifts)', async () => {
    const api = new MockArchitectureApi();
    const baselines = await api.listBaselines(PROJECT);
    expect(baselines.baselines.length).toBeGreaterThan(0);
    const comparison = await api.getBaselineComparison(baselines.baselines[0].id);
    expect(comparison.conformancePercent).toBeGreaterThan(0);
    expect(comparison.drifts.length).toBeGreaterThan(0);
  });

  it('lista candidatos de reuso do portfólio filtrando por capacidade', async () => {
    const api = new MockArchitectureApi();
    const reuse = await api.getPortfolioReuse(PROJECT, 'Cobrança');
    expect(reuse.candidateCount).toBe(reuse.candidates.length);
    expect(reuse.candidates.every((c) => c.matchedCapabilities.includes('Cobrança'))).toBe(true);
  });
});
