import type { ArchitectureElement, ArchitectureRelationship } from '../api/types';
import { classifyElement } from './classification';

/**
 * Layout determinístico do canvas (SVG). Sem drag, sem física: agrupa os
 * elementos em COLUNAS por camada (contexto → aplicação → dados →
 * tecnologia → motivação/outros) e empilha em linhas. Estável entre
 * renders — testável por coordenadas.
 */

export interface LaidOutNode {
  element: ArchitectureElement;
  x: number;
  y: number;
  width: number;
  height: number;
  column: number;
}

export interface LaidOutEdge {
  relationship: ArchitectureRelationship;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
}

export interface CanvasLayout {
  nodes: LaidOutNode[];
  edges: LaidOutEdge[];
  width: number;
  height: number;
}

export const NODE_WIDTH = 180;
export const NODE_HEIGHT = 72;
const COL_GAP = 96;
const ROW_GAP = 32;
const PADDING = 40;

/** Ordem das colunas por afinidade de camada. */
const COLUMN_ORDER = [
  'motivation',
  'context',
  'business',
  'application',
  'component',
  'data',
  'technology',
  'deployment',
  'other',
] as const;
type ColumnKey = (typeof COLUMN_ORDER)[number];

function columnFor(el: ArchitectureElement): ColumnKey {
  const c = classifyElement(el);
  if (c.archimate === 'motivation') return 'motivation';
  if (c.c4 === 'context') return 'context';
  if (c.archimate === 'business') return 'business';
  if (c.archimate === 'application' || c.c4 === 'container') return 'application';
  if (c.c4 === 'component') return 'component';
  if (c.archimate === 'data') return 'data';
  if (c.archimate === 'technology') return 'technology';
  if (c.c4 === 'deployment') return 'deployment';
  return 'other';
}

/**
 * Calcula posições dos nós e endpoints das arestas. Elementos são
 * ordenados por nome dentro da coluna para estabilidade.
 */
export function layoutModel(
  elements: ArchitectureElement[],
  relationships: ArchitectureRelationship[],
): CanvasLayout {
  const byColumn = new Map<ColumnKey, ArchitectureElement[]>();
  for (const el of elements) {
    const key = columnFor(el);
    const list = byColumn.get(key) ?? [];
    list.push(el);
    byColumn.set(key, list);
  }

  const usedColumns = COLUMN_ORDER.filter((c) => byColumn.has(c));
  const nodes: LaidOutNode[] = [];
  const positions = new Map<string, LaidOutNode>();
  let maxRows = 0;

  usedColumns.forEach((columnKey, columnIndex) => {
    const list = (byColumn.get(columnKey) ?? [])
      .slice()
      .sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
    maxRows = Math.max(maxRows, list.length);
    const x = PADDING + columnIndex * (NODE_WIDTH + COL_GAP);
    list.forEach((element, rowIndex) => {
      const y = PADDING + rowIndex * (NODE_HEIGHT + ROW_GAP);
      const node: LaidOutNode = {
        element,
        x,
        y,
        width: NODE_WIDTH,
        height: NODE_HEIGHT,
        column: columnIndex,
      };
      nodes.push(node);
      positions.set(element.id, node);
    });
  });

  const edges: LaidOutEdge[] = [];
  for (const relationship of relationships) {
    const from = positions.get(relationship.sourceId);
    const to = positions.get(relationship.targetId);
    if (!from || !to) continue;
    edges.push({
      relationship,
      x1: from.x + from.width / 2,
      y1: from.y + from.height / 2,
      x2: to.x + to.width / 2,
      y2: to.y + to.height / 2,
    });
  }

  const width =
    PADDING * 2 + Math.max(1, usedColumns.length) * NODE_WIDTH + Math.max(0, usedColumns.length - 1) * COL_GAP;
  const height = PADDING * 2 + Math.max(1, maxRows) * NODE_HEIGHT + Math.max(0, maxRows - 1) * ROW_GAP;

  return { nodes, edges, width, height };
}
