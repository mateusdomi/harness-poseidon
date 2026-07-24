import { useTranslation } from 'react-i18next';

import { cn } from '@/lib/utils';
import type { ArchitectureElement, ArchitectureRelationship } from '../api/types';
import { classifyElement } from '../model/classification';
import { layoutModel, NODE_HEIGHT, NODE_WIDTH } from '../model/layout';

/** Cor de acento por camada — reforça a leitura da view. */
function accentClass(el: ArchitectureElement): string {
  const c = classifyElement(el);
  if (c.archimate === 'motivation') return 'border-l-info';
  if (c.c4 === 'context' || c.archimate === 'business') return 'border-l-brand';
  if (c.archimate === 'application' || c.c4 === 'container' || c.c4 === 'component')
    return 'border-l-accent';
  if (c.archimate === 'data') return 'border-l-warning';
  if (c.archimate === 'technology' || c.c4 === 'deployment') return 'border-l-success';
  return 'border-l-border-strong';
}

export interface ArchitectureCanvasProps {
  elements: ArchitectureElement[];
  relationships: ArchitectureRelationship[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

/**
 * Canvas do Studio: nós HTML (acessíveis, tema-aware) posicionados por um
 * layout determinístico sobre uma camada SVG de arestas. Diagramas são
 * VIEWS do mesmo grafo — trocar a view re-projeta os nós.
 */
export function ArchitectureCanvas({
  elements,
  relationships,
  selectedId,
  onSelect,
}: ArchitectureCanvasProps) {
  const { t } = useTranslation();
  const { nodes, edges, width, height } = layoutModel(elements, relationships);

  if (nodes.length === 0) {
    return (
      <div
        className="flex min-h-64 items-center justify-center rounded-lg border border-dashed border-border-strong text-sm text-foreground-muted"
        data-testid="architecture-canvas-empty"
      >
        {t('architecture.canvas.empty')}
      </div>
    );
  }

  return (
    <div className="overflow-auto rounded-lg border border-border-strong bg-surface-elevated">
      <div
        className="relative"
        style={{ width, height }}
        role="group"
        aria-label={t('architecture.canvas.label')}
        data-testid="architecture-canvas"
      >
        <svg
          className="pointer-events-none absolute inset-0"
          width={width}
          height={height}
          aria-hidden="true"
        >
          <defs>
            <marker
              id="arch-arrow"
              viewBox="0 0 10 10"
              refX="9"
              refY="5"
              markerWidth="7"
              markerHeight="7"
              orient="auto-start-reverse"
            >
              <path d="M 0 0 L 10 5 L 0 10 z" className="fill-foreground-muted" />
            </marker>
          </defs>
          {edges.map((edge) => (
            <line
              key={edge.relationship.id}
              x1={edge.x1}
              y1={edge.y1}
              x2={edge.x2}
              y2={edge.y2}
              className="stroke-border-strong"
              strokeWidth={1.5}
              markerEnd="url(#arch-arrow)"
            />
          ))}
        </svg>

        {nodes.map((node) => {
          const isSelected = node.element.id === selectedId;
          return (
            <button
              key={node.element.id}
              type="button"
              onClick={() => onSelect(node.element.id)}
              aria-pressed={isSelected}
              className={cn(
                'absolute flex flex-col justify-center gap-0.5 rounded-md border border-l-4 bg-surface px-3 py-2 text-left shadow-sm transition-colors',
                'hover:border-brand focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand',
                accentClass(node.element),
                isSelected ? 'ring-2 ring-brand' : 'border-border-strong',
              )}
              style={{ left: node.x, top: node.y, width: NODE_WIDTH, height: NODE_HEIGHT }}
            >
              <span className="truncate text-sm font-medium text-foreground">
                {node.element.name}
              </span>
              <span className="truncate text-xs text-foreground-muted">{node.element.kind}</span>
              {node.element.locked ? (
                <span className="text-[10px] uppercase tracking-wide text-warning">
                  {t('architecture.inspector.locked')}
                </span>
              ) : null}
            </button>
          );
        })}
      </div>
    </div>
  );
}
