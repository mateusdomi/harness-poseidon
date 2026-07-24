import type { ArchitectureElement, ArchitectureRelationship } from '../api/types';

/**
 * Classificação e PROJEÇÃO do modelo arquitetural (ARC-04).
 *
 * O modelo é UM grafo de elementos + relacionamentos. Cada "view" (C4,
 * ArchiMate, extras) é uma PROJEÇÃO — um filtro determinístico sobre o
 * mesmo grafo, não uma imagem. Toda a lógica aqui é pura e testável sem DOM.
 */

export type C4Layer = 'context' | 'container' | 'component' | 'deployment';
export type ArchiMateLayer =
  | 'business'
  | 'application'
  | 'data'
  | 'technology'
  | 'motivation';

/** Grupos do seletor de views. */
export type ViewGroup = 'model' | 'c4' | 'archimate' | 'extra';

/** Uma view = grupo + predicado sobre o elemento classificado. */
export interface ViewPreset {
  id: string;
  group: ViewGroup;
  /** Chave i18n do rótulo em `architecture.views.<id>`. */
  match: (el: ArchitectureElement, c: ElementClassification) => boolean;
}

export interface ElementClassification {
  c4: C4Layer | null;
  archimate: ArchiMateLayer | null;
  tags: string[];
}

const C4_KIND: Record<string, C4Layer> = {
  person: 'context',
  actor: 'context',
  user: 'context',
  softwaresystem: 'context',
  system: 'context',
  externalsystem: 'context',
  external: 'context',
  container: 'container',
  webapp: 'container',
  webapplication: 'container',
  spa: 'container',
  api: 'container',
  service: 'container',
  microservice: 'container',
  database: 'container',
  datastore: 'container',
  queue: 'container',
  topic: 'container',
  broker: 'container',
  component: 'component',
  module: 'component',
  controller: 'component',
  repository: 'component',
  deploymentnode: 'deployment',
  node: 'deployment',
  infrastructurenode: 'deployment',
  cluster: 'deployment',
  pod: 'deployment',
  device: 'deployment',
};

const ARCHIMATE_KIND: Record<string, ArchiMateLayer> = {
  businessactor: 'business',
  businessrole: 'business',
  businessprocess: 'business',
  businessservice: 'business',
  businessfunction: 'business',
  applicationcomponent: 'application',
  applicationservice: 'application',
  applicationfunction: 'application',
  service: 'application',
  microservice: 'application',
  dataobject: 'data',
  dataentity: 'data',
  database: 'data',
  datastore: 'data',
  artifact: 'data',
  node: 'technology',
  deploymentnode: 'technology',
  systemsoftware: 'technology',
  technologyservice: 'technology',
  runtime: 'technology',
  stakeholder: 'motivation',
  driver: 'motivation',
  goal: 'motivation',
  outcome: 'motivation',
  requirement: 'motivation',
  constraint: 'motivation',
  principle: 'motivation',
  assessment: 'motivation',
};

function normalizeKind(kind: string): string {
  return kind.toLowerCase().replace(/[\s_-]/g, '');
}

function readTags(el: ArchitectureElement): string[] {
  const raw = el.properties.tags ?? el.properties.tag ?? '';
  return raw
    .split(/[,;|]/)
    .map((t) => t.trim().toLowerCase())
    .filter(Boolean);
}

/** Classifica um elemento em camadas C4 / ArchiMate + tags. */
export function classifyElement(el: ArchitectureElement): ElementClassification {
  const kind = normalizeKind(el.kind);
  const c4 =
    (el.properties.c4 as C4Layer | undefined) ??
    (el.properties.layer && el.properties.layer in { context: 1, container: 1, component: 1, deployment: 1 }
      ? (el.properties.layer as C4Layer)
      : undefined) ??
    C4_KIND[kind] ??
    null;
  const archimate =
    (el.properties.archimate as ArchiMateLayer | undefined) ?? ARCHIMATE_KIND[kind] ?? null;
  return { c4, archimate, tags: readTags(el) };
}

function hasTag(c: ElementClassification, tag: string): boolean {
  return c.tags.includes(tag);
}

/**
 * Presets de view. C4 (5) + ArchiMate (5) + extras + "Modelo completo".
 * Cada preset é um filtro sobre o grafo compartilhado.
 */
export const VIEW_PRESETS: ViewPreset[] = [
  { id: 'model', group: 'model', match: () => true },

  { id: 'c4-context', group: 'c4', match: (_e, c) => c.c4 === 'context' },
  {
    id: 'c4-container',
    group: 'c4',
    match: (_e, c) => c.c4 === 'context' || c.c4 === 'container',
  },
  {
    id: 'c4-component',
    group: 'c4',
    match: (_e, c) => c.c4 === 'container' || c.c4 === 'component',
  },
  {
    id: 'c4-deployment',
    group: 'c4',
    match: (_e, c) => c.c4 === 'deployment' || c.c4 === 'container',
  },
  {
    id: 'c4-dynamic',
    group: 'c4',
    match: (_e, c) => c.c4 === 'container' || c.c4 === 'component' || c.c4 === 'context',
  },

  { id: 'archimate-business', group: 'archimate', match: (_e, c) => c.archimate === 'business' },
  {
    id: 'archimate-application',
    group: 'archimate',
    match: (_e, c) => c.archimate === 'application',
  },
  { id: 'archimate-data', group: 'archimate', match: (_e, c) => c.archimate === 'data' },
  {
    id: 'archimate-technology',
    group: 'archimate',
    match: (_e, c) => c.archimate === 'technology',
  },
  {
    id: 'archimate-motivation',
    group: 'archimate',
    match: (_e, c) => c.archimate === 'motivation',
  },

  {
    id: 'data',
    group: 'extra',
    match: (_e, c) => c.archimate === 'data' || hasTag(c, 'data') || hasTag(c, 'pii'),
  },
  {
    id: 'sequence',
    group: 'extra',
    match: (_e, c) => c.c4 === 'container' || c.c4 === 'component',
  },
  {
    id: 'security',
    group: 'extra',
    match: (el, c) =>
      hasTag(c, 'security') ||
      normalizeKind(el.kind) === 'trustboundary' ||
      normalizeKind(el.kind) === 'securityzone' ||
      el.properties.trustBoundary != null,
  },
  {
    id: 'infrastructure',
    group: 'extra',
    match: (_e, c) => c.c4 === 'deployment' || c.archimate === 'technology',
  },
  {
    id: 'network',
    group: 'extra',
    match: (el, c) =>
      hasTag(c, 'network') ||
      normalizeKind(el.kind) === 'network' ||
      normalizeKind(el.kind) === 'subnet',
  },
  { id: 'cost', group: 'extra', match: (el) => el.properties.cost != null || el.properties.monthlyCost != null },
  {
    id: 'observability',
    group: 'extra',
    match: (el, c) =>
      hasTag(c, 'observability') ||
      ['monitor', 'dashboard', 'observability', 'metric'].includes(normalizeKind(el.kind)),
  },
];

export function findPreset(id: string): ViewPreset {
  return VIEW_PRESETS.find((p) => p.id === id) ?? VIEW_PRESETS[0];
}

export interface ProjectedModel {
  elements: ArchitectureElement[];
  relationships: ArchitectureRelationship[];
}

/**
 * Projeta o grafo completo numa view: mantém os elementos que casam o
 * preset e os relacionamentos cujos DOIS extremos ficaram visíveis.
 */
export function projectModel(
  elements: ArchitectureElement[],
  relationships: ArchitectureRelationship[],
  presetId: string,
): ProjectedModel {
  const preset = findPreset(presetId);
  const visible = elements.filter((el) => preset.match(el, classifyElement(el)));
  const visibleIds = new Set(visible.map((el) => el.id));
  const edges = relationships.filter(
    (r) => visibleIds.has(r.sourceId) && visibleIds.has(r.targetId),
  );
  return { elements: visible, relationships: edges };
}
