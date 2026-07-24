import { ApiError, problemDetailsSchema } from '@/api';

import {
  architectureElementPageSchema,
  architectureElementSchema,
  architectureRelationshipListSchema,
  architectureRelationshipSchema,
  architectureViewListSchema,
  architectureViewSchema,
  type ArchitectureElement,
  type ArchitectureRelationship,
  type ArchitectureView,
  type ArchitectureViewSummary,
  type CreateElementInput,
  type CreateRelationshipInput,
  type CreateViewInput,
  type LockInput,
} from './types';
import { buildArchitectureFixture } from './mock-data';

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
