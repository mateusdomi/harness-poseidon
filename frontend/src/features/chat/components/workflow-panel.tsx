import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import {
  CircleCheck,
  CircleDashed,
  CircleMinus,
  FilePenLine,
  Hourglass,
  TriangleAlert,
  X,
  type LucideIcon,
} from 'lucide-react';

import { streams, type Document, type Gate, type Phase, type Ulid } from '@/api';
import { Badge, Button, Skeleton } from '@/design-system';
import {
  countDocumentsByHealth,
  DOCUMENT_HEALTH_FILTERS,
  documentHealth,
  documentsOfPhase,
  filterDocumentsByHealth,
  phaseProgress,
  type DocumentHealth,
  type DocumentHealthFilter,
} from '@/features/chat/lib/workflow-panel-derive';
import { useRealtimeStream } from '@/features/shared/hooks/use-realtime-stream';
import {
  useActiveRun,
  useProjectWorkflow,
  useRunDetails,
  useWorkflowDocuments,
  WORKFLOWS_PREFIX,
} from '@/features/workflows/hooks/use-workflows';
import { formatNumber } from '@/lib/format';
import { phaseStateVariant } from '@/lib/status';
import { cn } from '@/lib/utils';

/** Texto + ícone + cor por conceito documental (nunca só cor — D-068). */
const HEALTH_META: Record<DocumentHealth, { Icon: LucideIcon; className: string }> = {
  notProduced: { Icon: CircleDashed, className: 'text-foreground-muted' },
  produced: { Icon: FilePenLine, className: 'text-info' },
  awaitingApproval: { Icon: Hourglass, className: 'text-warning' },
  approved: { Icon: CircleCheck, className: 'text-success' },
  rejected: { Icon: TriangleAlert, className: 'text-error' },
  notApplicable: { Icon: CircleMinus, className: 'text-foreground-muted' },
};

/** Eventos do stream do projeto que mantêm o painel fresco (D-069). */
const PANEL_EVENT_TYPES = ['document.stateChanged', 'gate.changed', 'progress.updated'] as const;

/**
 * Dados do painel: workflow ativo do projeto → run corrente → fases,
 * gates e documentos. Reusa as queries da feature de workflows (mesmo
 * cache React Query, sem endpoints novos) e assina o stream do projeto.
 */
function useWorkflowPanel(projectId: Ulid | null) {
  const workflowQuery = useProjectWorkflow(projectId);
  const workflow = workflowQuery.data ?? null;
  const runQuery = useActiveRun(workflow?.id ?? null);
  const run = runQuery.data ?? null;
  const runDetails = useRunDetails(run?.id ?? null);
  const documentsQuery = useWorkflowDocuments(projectId);

  useRealtimeStream(projectId === null ? null : streams.project(projectId), {
    types: PANEL_EVENT_TYPES,
    invalidate: [WORKFLOWS_PREFIX],
  });

  return {
    hasWorkflow: workflow !== null && run !== null,
    phases: runDetails.phases,
    gates: runDetails.gates,
    documents: documentsQuery.data ?? [],
    isPending:
      workflowQuery.isLoading ||
      (workflow !== null && runQuery.isLoading) ||
      (run !== null && runDetails.isPending) ||
      documentsQuery.isLoading,
    isError:
      workflowQuery.isError || runQuery.isError || runDetails.isError || documentsQuery.isError,
    refetch: () => {
      void workflowQuery.refetch();
      void runQuery.refetch();
      runDetails.refetch();
      void documentsQuery.refetch();
    },
  };
}

/** Setas ↑/↓ movem o foco entre os cabeçalhos do acordeão (wrap). */
function handleHeaderKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
  event.preventDefault();
  const group = event.currentTarget.closest('[data-accordion-group]');
  const headers = Array.from(
    group?.querySelectorAll<HTMLButtonElement>('[data-accordion-header]') ?? [],
  );
  const index = headers.indexOf(event.currentTarget);
  if (index === -1) return;
  const next =
    event.key === 'ArrowDown'
      ? (headers[index + 1] ?? headers[0])
      : (headers[index - 1] ?? headers[headers.length - 1]);
  next?.focus();
}

interface PhaseAccordionProps {
  phase: Phase;
  gates: Gate[];
  documents: Document[];
  defaultOpen: boolean;
}

/** Fase como acordeão: nome + estado, barra de progresso e documentos. */
function PhaseAccordion({ phase, gates, documents, defaultOpen }: PhaseAccordionProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(defaultOpen);
  const [filter, setFilter] = useState<DocumentHealthFilter | null>(null);

  const contentId = `workflow-panel-phase-${phase.id}`;
  const progress = phaseProgress(phase, gates, documents);
  const phaseDocuments = documentsOfPhase(documents, phase);
  const counts = countDocumentsByHealth(phaseDocuments);
  const visibleDocuments = filterDocumentsByHealth(phaseDocuments, filter);

  return (
    <section className="rounded-lg border border-border">
      <h3>
        <button
          type="button"
          data-accordion-header
          aria-expanded={open}
          aria-controls={contentId}
          onClick={() => setOpen((value) => !value)}
          onKeyDown={handleHeaderKeyDown}
          className="flex min-h-11 w-full items-center gap-2 rounded-t-lg px-3 py-2 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <span className="min-w-0 flex-1 truncate text-sm font-semibold">{phase.name}</span>
          <Badge variant={phaseStateVariant(phase.state)}>
            {t(`status.phaseState.${phase.state}`)}
          </Badge>
          <span aria-hidden="true" className="text-xs text-foreground-muted">
            {open ? '▾' : '▸'}
          </span>
        </button>
      </h3>
      <div className="px-3 pb-2">
        <div className="flex items-center gap-2">
          <div
            role="progressbar"
            aria-label={t('chat.workflowPanel.phaseProgress', { phase: phase.name })}
            aria-valuenow={progress}
            aria-valuemin={0}
            aria-valuemax={100}
            className="h-1.5 flex-1 overflow-hidden rounded-full bg-surface-elevated"
          >
            <div
              className="h-full rounded-full bg-brand transition-[width]"
              style={{ width: `${progress}%` }}
            />
          </div>
          <span className="text-xs tabular-nums text-foreground-muted">
            {formatNumber(progress)}%
          </span>
        </div>
      </div>
      {open && (
        <div id={contentId} className="flex flex-col gap-2 border-t border-border px-3 py-2">
          {phaseDocuments.length > 0 && (
            <div
              role="group"
              aria-label={t('chat.workflowPanel.filters.label', { phase: phase.name })}
              className="flex flex-wrap gap-1.5"
            >
              {DOCUMENT_HEALTH_FILTERS.map((option) => (
                <button
                  key={option}
                  type="button"
                  aria-pressed={filter === option}
                  onClick={() => setFilter((current) => (current === option ? null : option))}
                  className={cn(
                    'min-h-11 rounded-full border px-2.5 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent sm:min-h-0 sm:py-1',
                    filter === option
                      ? 'border-brand bg-primary/10 font-semibold text-brand-strong ring-1 ring-inset ring-brand'
                      : 'border-border font-medium text-foreground-muted hover:text-foreground',
                  )}
                >
                  {t(`chat.workflowPanel.health.${option}`)} ({formatNumber(counts[option])})
                </button>
              ))}
            </div>
          )}
          {phaseDocuments.length === 0 ? (
            <p className="text-xs text-foreground-muted">{t('chat.workflowPanel.noDocuments')}</p>
          ) : visibleDocuments.length === 0 ? (
            <p className="text-xs text-foreground-muted">
              {t('chat.workflowPanel.noDocumentsFiltered')}
            </p>
          ) : (
            <ul aria-label={t('chat.workflowPanel.documentsLabel', { phase: phase.name })}>
              {visibleDocuments.map((doc) => {
                const health = documentHealth(doc);
                const { Icon, className } = HEALTH_META[health];
                return (
                  <li key={doc.id}>
                    <Link
                      to={`/documents?doc=${doc.id}`}
                      className="flex min-h-11 items-center gap-2 rounded-md px-1 py-1.5 text-xs hover:bg-surface-elevated focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                    >
                      <Icon aria-hidden="true" className={cn('size-4 shrink-0', className)} />
                      <span className="min-w-0 flex-1 truncate font-medium text-brand-strong">
                        {doc.title}
                      </span>
                      <span className={cn('shrink-0', className)}>
                        {t(`chat.workflowPanel.health.${health}`)}
                      </span>
                    </Link>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      )}
    </section>
  );
}

/**
 * Conteúdo do painel lateral de workflow (usado no aside desktop e no
 * drawer mobile): fases do run ativo de cima para baixo, cada uma um
 * acordeão com progresso derivado (D-069) e documentos por conceito
 * documental (D-068) com deep-link para o catálogo.
 */
export function WorkflowPanel({ projectId }: { projectId: Ulid | null }) {
  const { t } = useTranslation();
  const panel = useWorkflowPanel(projectId);

  if (panel.isPending) {
    return (
      <div className="flex flex-col gap-2" role="status" aria-label={t('common.states.loading')}>
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-16 w-full" />
        <Skeleton className="h-16 w-full" />
      </div>
    );
  }

  if (panel.isError) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" size="sm" onClick={panel.refetch}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  if (!panel.hasWorkflow) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p className="text-sm font-medium">{t('chat.workflowPanel.empty.title')}</p>
        <p className="text-sm text-foreground-muted">{t('chat.workflowPanel.empty.body')}</p>
        <Button asChild variant="outline" size="sm">
          <Link to="/workflows">{t('chat.workflowPanel.empty.cta')}</Link>
        </Button>
      </div>
    );
  }

  return (
    <div data-accordion-group className="flex flex-col gap-2">
      {panel.phases.map((phase) => (
        <PhaseAccordion
          key={phase.id}
          phase={phase}
          gates={panel.gates}
          documents={panel.documents}
          defaultOpen={phase.state === 'active'}
        />
      ))}
    </div>
  );
}

const FOCUSABLE =
  'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * Drawer mobile (<lg) do painel de workflow: role="dialog" modal, foco
 * preso, Esc fecha, backdrop fecha e o foco volta para quem abriu —
 * mesmo contrato de a11y do TaskDrawer (D-022).
 */
export function WorkflowPanelDrawer({
  projectId,
  onClose,
}: {
  projectId: Ulid | null;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const panelRef = useRef<HTMLDivElement>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    const previouslyFocused =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;
    (panel.querySelector<HTMLElement>(FOCUSABLE) ?? panel).focus();

    function handleKeyDown(event: globalThis.KeyboardEvent) {
      if (event.key === 'Escape') {
        event.stopPropagation();
        onCloseRef.current();
        return;
      }
      if (event.key !== 'Tab' || !panel) return;
      const focusable = [...panel.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
        (element) => element.offsetParent !== null,
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }

    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('keydown', handleKeyDown);
      previouslyFocused?.focus();
    };
  }, []);

  return (
    <div className="fixed inset-0 z-40">
      <button
        type="button"
        tabIndex={-1}
        aria-label={t('chat.workflowPanel.close')}
        onClick={onClose}
        className="absolute inset-0 cursor-default bg-background/70"
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={t('chat.workflowPanel.title')}
        tabIndex={-1}
        className="absolute right-0 top-0 flex h-full w-full max-w-sm flex-col gap-3 overflow-y-auto border-l border-border bg-background p-4 shadow-2xl"
      >
        <div className="flex items-center gap-2">
          <h2 className="flex-1 font-heading text-lg font-semibold">
            {t('chat.workflowPanel.title')}
          </h2>
          <button
            type="button"
            onClick={onClose}
            aria-label={t('chat.workflowPanel.close')}
            className="flex size-11 items-center justify-center rounded-md text-foreground-muted transition-colors hover:bg-surface-elevated hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
          >
            <X aria-hidden="true" className="size-5" />
          </button>
        </div>
        <WorkflowPanel projectId={projectId} />
      </div>
    </div>
  );
}
