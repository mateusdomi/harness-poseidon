import { ApiError, problemDetailsSchema } from '@/api';

import {
  architectureBaselineListSchema,
  architectureBaselineSchema,
  architectureElementPageSchema,
  architectureElementSchema,
  architecturePatternListSchema,
  architecturePatternSchema,
  architectureRelationshipListSchema,
  architectureRelationshipSchema,
  architectureViewListSchema,
  architectureViewSchema,
  baselineComparisonSchema,
  capabilityMapSchema,
  discoveryListSchema,
  discoverySummaryListSchema,
  domainMapSchema,
  integrationGraphSchema,
  portfolioReuseSchema,
  rationalizationReportSchema,
  system360Schema,
  systemCatalogSchema,
  systemHeatmapSchema,
  type ArchitectureBaseline,
  type ArchitectureBaselineList,
  type ArchitectureElement,
  type ArchitecturePattern,
  type ArchitecturePatternList,
  type ArchitectureRelationship,
  type ArchitectureView,
  type ArchitectureViewSummary,
  type BaselineComparison,
  type CapabilityMap,
  type CreateElementInput,
  type CreateRelationshipInput,
  type CreateViewInput,
  type DiscoveryList,
  type DiscoverySummaryList,
  type DomainMap,
  type IntegrationGraph,
  type LockInput,
  type PortfolioReuse,
  type RationalizationReport,
  type System360,
  type SystemCatalog,
  type SystemHeatmap,
} from './types';
import { buildArchitectureFixture } from './mock-data';
import {
  buildBaselineComparison,
  buildBaselines,
  buildCapabilityMap,
  buildDiscoveries,
  buildDiscoverySummary,
  buildDomainMap,
  buildHeatmap,
  buildInsights,
  buildIntegrationGraph,
  buildPatterns,
  buildPortfolioReuse,
  buildSystem360,
  buildSystemCatalog,
} from './hub-mock-data';

/**
 * Cliente do Architecture Hub consumido pelo Studio (ARC-04). Mesma
 * filosofia do resto do app: a UI só vê esta interface; trocar mock ↔ http
 * não exige tocar em componente algum.
 *
 * Nota de escopo (ARC-04): a API NÃO expõe PATCH de elemento — edição de
 * propriedades de elemento existente flui por PROPOSTAS (ARC-05). O Studio
 * cobre o que a API oferece: inspeção, lock/unlock, criação de elementos,
 * relacionamentos e views, e projeção do grafo.
 */
export interface ArchitectureApi {
  listElements(projectId: string): Promise<ArchitectureElement[]>;
  listRelationships(projectId: string): Promise<ArchitectureRelationship[]>;
  listViews(projectId: string): Promise<ArchitectureViewSummary[]>;
  getView(id: string): Promise<ArchitectureView>;
  createElement(input: CreateElementInput): Promise<ArchitectureElement>;
  createRelationship(input: CreateRelationshipInput): Promise<ArchitectureRelationship>;
  createView(input: CreateViewInput): Promise<ArchitectureView>;
  setElementLock(id: string, input: LockInput): Promise<ArchitectureElement>;

  /* ---- Architecture Hub (somente leitura) ---- */
  // ARC-02 Mapa Corporativo de Sistemas
  listSystems(projectId: string | null): Promise<SystemCatalog>;
  getDomainMap(projectId: string | null): Promise<DomainMap>;
  getCapabilityMap(projectId: string | null): Promise<CapabilityMap>;
  getIntegrationGraph(projectId: string | null): Promise<IntegrationGraph>;
  getHeatmap(projectId: string | null): Promise<SystemHeatmap>;
  // ARC-03 Sistema 360
  getSystemOverview(systemId: string): Promise<System360>;
  // ARC-06 Discovery
  listDiscoveries(projectId: string | null, systemId?: string | null): Promise<DiscoveryList>;
  getDiscoverySummary(
    projectId: string | null,
    systemId?: string | null,
  ): Promise<DiscoverySummaryList>;
  // ARC-07 Insights & Racionalização
  getInsights(projectId: string | null): Promise<RationalizationReport>;
  // ARC-08 Padrões & ADRs
  listPatterns(projectId: string | null, kind?: string | null): Promise<ArchitecturePatternList>;
  getPattern(id: string): Promise<ArchitecturePattern>;
  // ARC-10 Baselines & Conformidade
  listBaselines(projectId: string | null): Promise<ArchitectureBaselineList>;
  getBaseline(id: string): Promise<ArchitectureBaseline>;
  getBaselineComparison(id: string): Promise<BaselineComparison>;
  getPortfolioReuse(
    projectId: string | null,
    capability?: string | null,
    domain?: string | null,
  ): Promise<PortfolioReuse>;
}

/** Monta uma query string a partir de pares opcionais (ignora nulos/vazios). */
function query(params: Record<string, string | null | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== null && value !== undefined && value !== '') search.set(key, value);
  }
  const qs = search.toString();
  return qs ? `?${qs}` : '';
}

/* ------------------------------------------------------------------ */
/* HTTP                                                                */
/* ------------------------------------------------------------------ */

export interface HttpArchitectureApiOptions {
  baseUrl: string;
  fetchFn?: typeof fetch;
}

/** Implementação real: fetch em `/api/v1/architecture/*`, erros RFC 7807. */
export class HttpArchitectureApi implements ArchitectureApi {
  readonly #base: string;
  readonly #fetch: typeof fetch;

  constructor(options: HttpArchitectureApiOptions) {
    this.#base = `${options.baseUrl.replace(/\/$/, '')}/api/v1/architecture`;
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

  async listElements(projectId: string): Promise<ArchitectureElement[]> {
    const raw = await this.#request('GET', `/elements?projectId=${encodeURIComponent(projectId)}`);
    return architectureElementPageSchema.parse(raw).elements;
  }

  async listRelationships(projectId: string): Promise<ArchitectureRelationship[]> {
    const raw = await this.#request(
      'GET',
      `/relationships?projectId=${encodeURIComponent(projectId)}`,
    );
    return architectureRelationshipListSchema.parse(raw).relationships;
  }

  async listViews(projectId: string): Promise<ArchitectureViewSummary[]> {
    const raw = await this.#request('GET', `/views?projectId=${encodeURIComponent(projectId)}`);
    return architectureViewListSchema.parse(raw).views;
  }

  async getView(id: string): Promise<ArchitectureView> {
    return architectureViewSchema.parse(await this.#request('GET', `/views/${id}`));
  }

  async createElement(input: CreateElementInput): Promise<ArchitectureElement> {
    return architectureElementSchema.parse(await this.#request('POST', '/elements', input));
  }

  async createRelationship(input: CreateRelationshipInput): Promise<ArchitectureRelationship> {
    return architectureRelationshipSchema.parse(
      await this.#request('POST', '/relationships', input),
    );
  }

  async createView(input: CreateViewInput): Promise<ArchitectureView> {
    return architectureViewSchema.parse(await this.#request('POST', '/views', input));
  }

  async setElementLock(id: string, input: LockInput): Promise<ArchitectureElement> {
    return architectureElementSchema.parse(
      await this.#request('POST', `/elements/${id}/lock`, input),
    );
  }

  async listSystems(projectId: string | null): Promise<SystemCatalog> {
    return systemCatalogSchema.parse(await this.#request('GET', `/systems${query({ projectId })}`));
  }

  async getDomainMap(projectId: string | null): Promise<DomainMap> {
    return domainMapSchema.parse(await this.#request('GET', `/maps/domains${query({ projectId })}`));
  }

  async getCapabilityMap(projectId: string | null): Promise<CapabilityMap> {
    return capabilityMapSchema.parse(
      await this.#request('GET', `/maps/capabilities${query({ projectId })}`),
    );
  }

  async getIntegrationGraph(projectId: string | null): Promise<IntegrationGraph> {
    return integrationGraphSchema.parse(
      await this.#request('GET', `/maps/integration${query({ projectId })}`),
    );
  }

  async getHeatmap(projectId: string | null): Promise<SystemHeatmap> {
    return systemHeatmapSchema.parse(
      await this.#request('GET', `/maps/heatmap${query({ projectId })}`),
    );
  }

  async getSystemOverview(systemId: string): Promise<System360> {
    return system360Schema.parse(
      await this.#request('GET', `/systems/${encodeURIComponent(systemId)}/overview`),
    );
  }

  async listDiscoveries(
    projectId: string | null,
    systemId?: string | null,
  ): Promise<DiscoveryList> {
    return discoveryListSchema.parse(
      await this.#request('GET', `/discoveries${query({ projectId, systemId })}`),
    );
  }

  async getDiscoverySummary(
    projectId: string | null,
    systemId?: string | null,
  ): Promise<DiscoverySummaryList> {
    return discoverySummaryListSchema.parse(
      await this.#request('GET', `/discoveries/summary${query({ projectId, systemId })}`),
    );
  }

  async getInsights(projectId: string | null): Promise<RationalizationReport> {
    return rationalizationReportSchema.parse(
      await this.#request('GET', `/insights${query({ projectId })}`),
    );
  }

  async listPatterns(
    projectId: string | null,
    kind?: string | null,
  ): Promise<ArchitecturePatternList> {
    return architecturePatternListSchema.parse(
      await this.#request('GET', `/patterns${query({ projectId, kind })}`),
    );
  }

  async getPattern(id: string): Promise<ArchitecturePattern> {
    return architecturePatternSchema.parse(
      await this.#request('GET', `/patterns/${encodeURIComponent(id)}`),
    );
  }

  async listBaselines(projectId: string | null): Promise<ArchitectureBaselineList> {
    return architectureBaselineListSchema.parse(
      await this.#request('GET', `/baselines${query({ projectId })}`),
    );
  }

  async getBaseline(id: string): Promise<ArchitectureBaseline> {
    return architectureBaselineSchema.parse(
      await this.#request('GET', `/baselines/${encodeURIComponent(id)}`),
    );
  }

  async getBaselineComparison(id: string): Promise<BaselineComparison> {
    return baselineComparisonSchema.parse(
      await this.#request('GET', `/baselines/${encodeURIComponent(id)}/comparison`),
    );
  }

  async getPortfolioReuse(
    projectId: string | null,
    capability?: string | null,
    domain?: string | null,
  ): Promise<PortfolioReuse> {
    return portfolioReuseSchema.parse(
      await this.#request('GET', `/portfolio/reuse${query({ projectId, capability, domain })}`),
    );
  }
}

/* ------------------------------------------------------------------ */
/* MOCK                                                                */
/* ------------------------------------------------------------------ */

interface ProjectStore {
  elements: ArchitectureElement[];
  relationships: ArchitectureRelationship[];
  views: ArchitectureView[];
}

/**
 * Implementação em memória, determinística — semeada com um modelo rico
 * cobrindo C4 + ArchiMate + extras, para o Studio ter conteúdo em toda
 * view sem depender do backend. Usada nos testes e no modo `mock`.
 */
export class MockArchitectureApi implements ArchitectureApi {
  readonly #byProject = new Map<string, ProjectStore>();
  #seq = 0;

  constructor(seed?: { projectId: string; store: ProjectStore }) {
    if (seed) this.#byProject.set(seed.projectId, seed.store);
  }

  #store(projectId: string): ProjectStore {
    let store = this.#byProject.get(projectId);
    if (!store) {
      store = buildArchitectureFixture(projectId);
      this.#byProject.set(projectId, store);
    }
    return store;
  }

  #nextId(prefix: string): string {
    this.#seq += 1;
    return `${prefix}-${this.#seq.toString().padStart(6, '0')}`;
  }

  async listElements(projectId: string): Promise<ArchitectureElement[]> {
    return this.#store(projectId).elements.slice();
  }

  async listRelationships(projectId: string): Promise<ArchitectureRelationship[]> {
    return this.#store(projectId).relationships.slice();
  }

  async listViews(projectId: string): Promise<ArchitectureViewSummary[]> {
    return this.#store(projectId).views.map((v) => ({
      id: v.id,
      projectId: v.projectId,
      name: v.name,
      notation: v.notation,
      elementCount: v.elements.length,
    }));
  }

  async getView(id: string): Promise<ArchitectureView> {
    for (const store of this.#byProject.values()) {
      const view = store.views.find((v) => v.id === id);
      if (view) return structuredClone(view);
    }
    throw ApiError.of(404, 'View não encontrada');
  }

  async createElement(input: CreateElementInput): Promise<ArchitectureElement> {
    const element: ArchitectureElement = {
      id: this.#nextId('el'),
      projectId: input.projectId,
      kind: input.kind,
      name: input.name,
      description: input.description,
      properties: { ...input.properties },
      state: 'proposed',
      locked: false,
      version: 1,
    };
    this.#store(input.projectId).elements.push(element);
    return { ...element };
  }

  async createRelationship(input: CreateRelationshipInput): Promise<ArchitectureRelationship> {
    const rel: ArchitectureRelationship = {
      id: this.#nextId('rel'),
      projectId: input.projectId,
      sourceId: input.sourceId,
      targetId: input.targetId,
      kind: input.kind,
      properties: { ...input.properties },
      state: 'proposed',
      version: 1,
    };
    this.#store(input.projectId).relationships.push(rel);
    return { ...rel };
  }

  async createView(input: CreateViewInput): Promise<ArchitectureView> {
    const store = this.#store(input.projectId);
    const chosen = new Set(input.elementIds);
    const elements = store.elements.filter((el) => chosen.has(el.id));
    const relationships = store.relationships.filter(
      (r) => chosen.has(r.sourceId) && chosen.has(r.targetId),
    );
    const view: ArchitectureView = {
      id: this.#nextId('view'),
      projectId: input.projectId,
      name: input.name,
      description: input.description,
      notation: input.notation,
      elements,
      relationships,
    };
    store.views.push(view);
    return structuredClone(view);
  }

  async setElementLock(id: string, input: LockInput): Promise<ArchitectureElement> {
    for (const store of this.#byProject.values()) {
      const element = store.elements.find((el) => el.id === id);
      if (element) {
        element.locked = input.locked;
        element.version += 1;
        return { ...element };
      }
    }
    throw ApiError.of(404, 'Elemento não encontrado');
  }

  async listSystems(projectId: string | null): Promise<SystemCatalog> {
    return buildSystemCatalog(projectId);
  }

  async getDomainMap(projectId: string | null): Promise<DomainMap> {
    return buildDomainMap(projectId);
  }

  async getCapabilityMap(projectId: string | null): Promise<CapabilityMap> {
    return buildCapabilityMap(projectId);
  }

  async getIntegrationGraph(projectId: string | null): Promise<IntegrationGraph> {
    return buildIntegrationGraph(projectId);
  }

  async getHeatmap(projectId: string | null): Promise<SystemHeatmap> {
    return buildHeatmap(projectId);
  }

  async getSystemOverview(systemId: string): Promise<System360> {
    const projectId = systemId.includes('::') ? systemId.split('::')[0] : null;
    const overview = buildSystem360(projectId, systemId);
    if (!overview) throw ApiError.of(404, 'Sistema não encontrado');
    return overview;
  }

  async listDiscoveries(
    projectId: string | null,
    systemId?: string | null,
  ): Promise<DiscoveryList> {
    let discoveries = buildDiscoveries(projectId);
    if (systemId) discoveries = discoveries.filter((d) => d.systemId === systemId);
    return { total: discoveries.length, nextCursor: null, discoveries };
  }

  async getDiscoverySummary(
    projectId: string | null,
    systemId?: string | null,
  ): Promise<DiscoverySummaryList> {
    const summary = buildDiscoverySummary(projectId);
    if (!systemId) return summary;
    const subjects = summary.subjects.filter((s) => s.systemId === systemId);
    return { total: subjects.length, subjects };
  }

  async getInsights(projectId: string | null): Promise<RationalizationReport> {
    return buildInsights(projectId);
  }

  async listPatterns(
    projectId: string | null,
    kind?: string | null,
  ): Promise<ArchitecturePatternList> {
    let items = buildPatterns(projectId);
    if (kind) items = items.filter((p) => p.kind === kind);
    return { total: items.length, nextCursor: null, items };
  }

  async getPattern(id: string): Promise<ArchitecturePattern> {
    const projectId = id.includes('::') ? id.split('::')[0] : null;
    const pattern = buildPatterns(projectId).find((p) => p.id === id);
    if (!pattern) throw ApiError.of(404, 'Padrão não encontrado');
    return pattern;
  }

  async listBaselines(projectId: string | null): Promise<ArchitectureBaselineList> {
    const baselines = buildBaselines(projectId ?? 'default');
    return { total: baselines.length, baselines };
  }

  async getBaseline(id: string): Promise<ArchitectureBaseline> {
    const projectId = id.includes('::') ? id.split('::')[0] : 'default';
    const baseline = buildBaselines(projectId).find((b) => b.id === id);
    if (!baseline) throw ApiError.of(404, 'Baseline não encontrada');
    return baseline;
  }

  async getBaselineComparison(id: string): Promise<BaselineComparison> {
    return buildBaselineComparison(id);
  }

  async getPortfolioReuse(
    projectId: string | null,
    capability?: string | null,
    domain?: string | null,
  ): Promise<PortfolioReuse> {
    return buildPortfolioReuse(projectId, capability ?? null, domain ?? null);
  }
}

/* ------------------------------------------------------------------ */
/* FACTORY                                                             */
/* ------------------------------------------------------------------ */

export function createArchitectureApi(
  mode: string = import.meta.env.VITE_API_MODE ?? 'mock',
): ArchitectureApi {
  if (mode === 'http') {
    const baseUrl = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5001';
    return new HttpArchitectureApi({ baseUrl });
  }
  return new MockArchitectureApi();
}
