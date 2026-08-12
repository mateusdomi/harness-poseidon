import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import {
  Activity,
  AlertTriangle,
  ClipboardCheck,
  CircleCheck,
  CircleDashed,
  CircleMinus,
  FilePenLine,
  Hourglass,
  Info,
  TriangleAlert,
  PackageCheck,
  X,
  type LucideIcon,
} from 'lucide-react';

import {
  streams,
  type Approval,
  type Document,
  type Gate,
  type Phase,
  type Task,
  type Ulid,
} from '@/api';
import { useApi } from '@/app/api-context';
import { Badge, Button, Skeleton, Tooltip } from '@/design-system';
import {
  countDocumentsByHealth,
  DOCUMENT_HEALTH_FILTERS,
  documentHealth,
  documentsOfPhase,
  expectedArtifactHealth,
  expectedArtifactsForPhase,
  filterDocumentsByHealth,
  phaseProgressEvidence,
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
import { useV3ProjectContext } from '@/features/projects/hooks/use-v3-understand';
import {
  projectV3Lifecycle,
  V3_LIFECYCLE_MACROS,
  v3LifecycleMacroIndex,
} from '@/features/projects/lib/v3-lifecycle';

/** Texto + ícone + cor por conceito documental (nunca só cor — D-068). */
const HEALTH_META: Record<DocumentHealth, { Icon: LucideIcon; className: string }> = {
  planned: { Icon: CircleDashed, className: 'text-foreground-muted' },
  notStarted: { Icon: CircleDashed, className: 'text-foreground-muted' },
  inProduction: { Icon: FilePenLine, className: 'text-info' },
  produced: { Icon: FilePenLine, className: 'text-info' },
  inReview: { Icon: Hourglass, className: 'text-warning' },
  approved: { Icon: CircleCheck, className: 'text-success' },
  rejected: { Icon: TriangleAlert, className: 'text-error' },
  notApplicable: { Icon: CircleMinus, className: 'text-foreground-muted' },
};

/** Eventos do stream do projeto que mantêm o painel fresco (D-069). */
const PANEL_EVENT_TYPES = ['document.stateChanged', 'gate.changed', 'progress.updated'] as const;

function V3LifecyclePanel({
  lifecycle,
  acceptanceCriteriaCount,
  openQuestionsCount,
}: {
  lifecycle: string;
  acceptanceCriteriaCount: number;
  openQuestionsCount: number;
}) {
  const { t } = useTranslation();
  const projection = projectV3Lifecycle(lifecycle);
  const activeIndex = Math.max(0, v3LifecycleMacroIndex(projection.macro));
  return (
    <div className="flex flex-col gap-4">
      <section className="rounded-xl border border-brand/30 bg-primary/5 p-4">
        <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-brand-strong">
          <Activity aria-hidden="true" className="size-4" />
          {t('chat.projectPanel.now')}
        </div>
        <div className="mt-2 flex flex-wrap items-center justify-between gap-2">
          <h3 className="font-heading text-lg font-semibold">{projection.macroLabel}</h3>
          <Badge variant={projection.badge}>{projection.statusLabel}</Badge>
        </div>
        {projection.state === 'READY_FOR_HUMAN_ACCEPTANCE' ? (
          <p className="mt-2 text-sm text-foreground-muted">
            {t('chat.workflowPanel.v3Lifecycle.handoffReady')}
          </p>
        ) : null}
      </section>

      <ol aria-label={t('chat.workflowPanel.v3Lifecycle.label')} className="flex flex-col gap-2">
        {V3_LIFECYCLE_MACROS.map((stage, index) => {
          const done = index < activeIndex || projection.state === 'HUMAN_ACCEPTED';
          const active = index === activeIndex && projection.state !== 'HUMAN_ACCEPTED';
          return (
            <li
              key={stage.id}
              className={cn(
                'flex min-h-11 items-center gap-2 rounded-lg border px-3 py-2 text-sm',
                active
                  ? 'border-brand bg-primary/10 text-brand-strong'
                  : done
                    ? 'border-success/40 bg-success/10 text-success'
                    : 'border-border bg-surface-elevated/30 text-foreground-muted',
              )}
            >
              <span aria-hidden="true" className="w-5 text-center">
                {done ? '✓' : active ? '●' : '○'}
              </span>
              <span className="font-medium">{stage.label}</span>
            </li>
          );
        })}
      </ol>

      <section aria-labelledby="chat-project-validation" className="flex flex-col gap-2">
        <h3 id="chat-project-validation" className="flex items-center gap-2 text-sm font-semibold">
          <ClipboardCheck aria-hidden="true" className="size-4 text-info" />
          {t('chat.projectPanel.validation')}
        </h3>
        <dl className="grid grid-cols-2 gap-2">
          <div className="rounded-lg border border-border bg-surface-elevated/40 p-3">
            <dt className="text-xs text-foreground-muted">
              {t('chat.projectPanel.acceptanceCriteria')}
            </dt>
            <dd className="mt-1">
              <Badge variant={acceptanceCriteriaCount > 0 ? 'success' : 'outline'}>
                {acceptanceCriteriaCount}
              </Badge>
            </dd>
          </div>
          <div className="rounded-lg border border-border bg-surface-elevated/40 p-3">
            <dt className="text-xs text-foreground-muted">
              {t('chat.projectPanel.blockers')}
            </dt>
            <dd className="mt-1">
              <Badge variant={openQuestionsCount > 0 ? 'warning' : 'success'}>
                {openQuestionsCount}
              </Badge>
            </dd>
          </div>
        </dl>
      </section>
    </div>
  );
}

/**
 * Dados do painel: workflow ativo do projeto → run corrente → fases,
 * gates e documentos. Reusa as queries da feature de workflows (mesmo
 * cache React Query, sem endpoints novos) e assina o stream do projeto.
 */
function useWorkflowPanel(projectId: Ulid | null) {
  const api = useApi();
  const workflowQuery = useProjectWorkflow(projectId);
  const workflow = workflowQuery.data ?? null;
  const runQuery = useActiveRun(workflow?.id ?? null);
  const run = runQuery.data ?? null;
  const runDetails = useRunDetails(run?.id ?? null);
  const documentsQuery = useWorkflowDocuments(projectId);
  const tasksQuery = useQuery({
    queryKey: ['chat', 'workflow-panel', 'tasks', projectId ?? 'none'] as const,
    queryFn: async (): Promise<Task[]> =>
      (await api.list('tasks', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });
  const approvalsQuery = useQuery({
    queryKey: ['chat', 'workflow-panel', 'approvals', projectId ?? 'none'] as const,
    queryFn: async (): Promise<Approval[]> =>
      (await api.list('approvals', { filter: { projectId: projectId! } })).items,
    enabled: projectId !== null,
  });

  useRealtimeStream(projectId === null ? null : streams.project(projectId), {
    types: PANEL_EVENT_TYPES,
    invalidate: [WORKFLOWS_PREFIX],
  });

  return {
    hasWorkflow: workflow !== null && run !== null,
    // RN-02: todo projeto tem um workflow atribuído automaticamente. Distinguimos o caso normal
    // "workflow vinculado, execução ainda não iniciada" (sem run) do caso de exceção "sem vínculo".
    hasBinding: workflow !== null,
    phases: runDetails.phases,
    gates: runDetails.gates,
    documents: documentsQuery.data ?? [],
    tasks: tasksQuery.data ?? [],
    approvals: approvalsQuery.data ?? [],
    isPending:
      workflowQuery.isLoading ||
      (workflow !== null && runQuery.isLoading) ||
      (run !== null && runDetails.isPending) ||
      documentsQuery.isLoading ||
      tasksQuery.isLoading ||
      approvalsQuery.isLoading,
    isError:
      workflowQuery.isError ||
      runQuery.isError ||
      runDetails.isError ||
      documentsQuery.isError ||
      tasksQuery.isError ||
      approvalsQuery.isError,
    refetch: () => {
      void workflowQuery.refetch();
      void runQuery.refetch();
      runDetails.refetch();
      void documentsQuery.refetch();
      void tasksQuery.refetch();
      void approvalsQuery.refetch();
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
  tasks: Task[];
  approvals: Approval[];
  defaultOpen: boolean;
}

function BusinessProjectSummary({
  phases,
  documents,
  tasks,
  approvals,
}: {
  phases: Phase[];
  documents: Document[];
  tasks: Task[];
  approvals: Approval[];
}) {
  const { t } = useTranslation();
  const ordered = [...phases].sort((left, right) => left.order - right.order);
  const current =
    ordered.find((phase) => phase.state === 'active') ??
    ordered.find((phase) => phase.state === 'pending') ??
    ordered.at(-1) ??
    null;
  const deliveries = current ? documentsOfPhase(documents, current) : [];
  const visibleDeliveries = deliveries
    .filter((document) => documentHealth(document) !== 'notApplicable')
    .slice(0, 3);
  const blocked = tasks.filter((task) => task.state === 'blocked').length;
  const decisions = approvals.filter((approval) => approval.state === 'pending').length;

  if (!current) {
    return <p className="text-sm text-foreground-muted">{t('chat.projectPanel.noProgress')}</p>;
  }

  return (
    <div className="flex flex-col gap-4">
      <section className="rounded-xl border border-brand/30 bg-primary/5 p-4">
        <div className="flex items-center gap-2 text-xs font-semibold uppercase tracking-wide text-brand-strong">
          <Activity aria-hidden="true" className="size-4" />
          {t('chat.projectPanel.now')}
        </div>
        <div className="mt-2 flex flex-wrap items-center justify-between gap-2">
          <h3 className="font-heading text-lg font-semibold">{current.name}</h3>
          <Badge variant={phaseStateVariant(current.state)}>
            {t(`chat.projectPanel.states.${current.state}`)}
          </Badge>
        </div>
        <div
          role="progressbar"
          aria-label={t('chat.projectPanel.progressLabel', { name: current.name })}
          aria-valuenow={current.progress.percent}
          aria-valuemin={0}
          aria-valuemax={100}
          className="mt-3 h-2 overflow-hidden rounded-full bg-surface-elevated"
        >
          <div
            className="h-full rounded-full bg-gradient-brand"
            style={{ width: `${current.progress.percent}%` }}
          />
        </div>
        <p className="mt-2 text-xs text-foreground-muted">
          {t('chat.projectPanel.progress', { percent: current.progress.percent })}
        </p>
      </section>

      <section aria-labelledby="chat-project-deliveries" className="flex flex-col gap-2">
        <h3 id="chat-project-deliveries" className="flex items-center gap-2 text-sm font-semibold">
          <PackageCheck aria-hidden="true" className="size-4 text-info" />
          {t('chat.projectPanel.deliveries')}
        </h3>
        {visibleDeliveries.length === 0 ? (
          <p className="text-sm text-foreground-muted">
            {t('chat.projectPanel.noDeliveries')}
          </p>
        ) : (
          <ul className="flex flex-col gap-2">
            {visibleDeliveries.map((document) => {
              const health = documentHealth(document);
              const { Icon, className } = HEALTH_META[health];
              return (
                <li key={document.id}>
                  <Link
                    to={`/documents?doc=${document.id}`}
                    className="flex min-h-11 items-center gap-2 rounded-lg border border-border bg-surface-elevated/40 px-3 py-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
                  >
                    <Icon aria-hidden="true" className={cn('size-4 shrink-0', className)} />
                    <span className="min-w-0 flex-1 truncate font-medium">{document.title}</span>
                    <span className={cn('shrink-0 text-xs', className)}>
                      {t(`chat.workflowPanel.health.${health}`)}
                    </span>
                  </Link>
                </li>
              );
            })}
          </ul>
        )}
      </section>

      <section aria-labelledby="chat-project-pending" className="flex flex-col gap-2">
        <h3 id="chat-project-pending" className="flex items-center gap-2 text-sm font-semibold">
          <ClipboardCheck aria-hidden="true" className="size-4 text-warning" />
          {t('chat.projectPanel.pending')}
        </h3>
        <dl className="grid grid-cols-2 gap-2">
          <div className="rounded-lg border border-border bg-surface-elevated/40 p-3">
            <dt className="text-xs text-foreground-muted">
              {t('chat.projectPanel.humanDecisions')}
            </dt>
            <dd className="mt-1">
              <Badge variant={decisions > 0 ? 'warning' : 'success'}>{decisions}</Badge>
            </dd>
          </div>
          <div className="rounded-lg border border-border bg-surface-elevated/40 p-3">
            <dt className="text-xs text-foreground-muted">
              {t('chat.projectPanel.blockers')}
            </dt>
            <dd className="mt-1">
              <Badge variant={blocked > 0 ? 'error' : 'success'}>{blocked}</Badge>
            </dd>
          </div>
        </dl>
        {blocked > 0 && (
          <p className="flex items-start gap-2 text-xs text-foreground-muted">
            <AlertTriangle aria-hidden="true" className="mt-0.5 size-3.5 shrink-0 text-warning" />
            {t('chat.projectPanel.blockedHelp')}
          </p>
        )}
      </section>
    </div>
  );
}

/** Fase como acordeão: nome + estado, barra de progresso e documentos. */
function PhaseAccordion({
  phase,
  gates,
  documents,
  tasks,
  approvals,
  defaultOpen,
}: PhaseAccordionProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(defaultOpen);
  const [filter, setFilter] = useState<DocumentHealthFilter | null>(null);

  const contentId = `workflow-panel-phase-${phase.id}`;
  const progress = phaseProgressEvidence(phase, gates, documents, tasks, approvals);
  const phaseDocuments = documentsOfPhase(documents, phase);
  const counts = countDocumentsByHealth(phaseDocuments);
  const visibleDocuments = filterDocumentsByHealth(phaseDocuments, filter);
  const expectedArtifacts = expectedArtifactsForPhase(phase);
  const missingExpectedArtifacts = expectedArtifacts.filter((artifact) => {
    const normalized = artifact.toLocaleLowerCase('pt-BR');
    return !phaseDocuments.some((document) => {
      const title = document.title.toLocaleLowerCase('pt-BR');
      return title.includes(normalized) || normalized.includes(title);
    });
  });
  const progressTooltip = t('chat.workflowPanel.progressEvidence.tooltip', {
    completed: progress.completed,
    total: progress.total,
    tasksDone: progress.tasks.completed,
    tasksTotal: progress.tasks.total,
    documentsDone: progress.documents.completed,
    documentsTotal: progress.documents.total,
    gatesDone: progress.gates.completed,
    gatesTotal: progress.gates.total,
    approvalsDone: progress.approvals.completed,
    approvalsTotal: progress.approvals.total,
  });

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
            aria-valuenow={progress.percent}
            aria-valuemin={0}
            aria-valuemax={100}
            className="h-1.5 flex-1 overflow-hidden rounded-full bg-surface-elevated"
          >
            <div
              className="h-full rounded-full bg-brand transition-[width]"
              style={{ width: `${progress.percent}%` }}
            />
          </div>
          <span className="text-xs tabular-nums text-foreground-muted">
            {formatNumber(progress.percent)}%
          </span>
          <Tooltip label={progressTooltip}>
            <button
              type="button"
              aria-label={progressTooltip}
              className="rounded-full text-foreground-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
            >
              <Info aria-hidden="true" className="size-3.5" />
            </button>
          </Tooltip>
        </div>
        <p className="mt-1 text-xs text-foreground-muted">
          {progress.phaseStateFallback
            ? t('chat.workflowPanel.progressEvidence.phaseFallback')
            : t('chat.workflowPanel.progressEvidence.fraction', {
                completed: progress.completed,
                total: progress.total,
              })}
        </p>
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
                  data-slot="tag"
                  className={cn(
                    'min-h-11 rounded-full border px-2.5 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent md:min-h-0 md:py-1',
                    filter === option
                      ? 'border-brand bg-primary/10 font-semibold text-brand-strong ring-1 ring-inset ring-brand'
                      : 'border-info/50 bg-info/10 font-medium text-info hover:bg-info/20',
                  )}
                >
                  {t(`chat.workflowPanel.health.${option}`)} ({formatNumber(counts[option])})
                </button>
              ))}
            </div>
          )}
          {missingExpectedArtifacts.length > 0 && (
            <div className="flex flex-col gap-1.5">
              <h4 className="text-xs font-semibold">
                {t('chat.workflowPanel.expectedArtifacts')}
              </h4>
              <ul className="flex flex-col gap-1">
                {missingExpectedArtifacts.map((artifact) => {
                  const health = expectedArtifactHealth(phase, artifact);
                  const { Icon, className } = HEALTH_META[health];
                  return (
                    <li key={artifact} className="flex min-h-9 items-center gap-2 text-xs">
                      <Icon aria-hidden="true" className={cn('size-4 shrink-0', className)} />
                      <span className="min-w-0 flex-1">{artifact}</span>
                      <span className={cn('shrink-0', className)}>
                        {t(`chat.workflowPanel.health.${health}`)}
                      </span>
                    </li>
                  );
                })}
              </ul>
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
export function WorkflowPanel({
  projectId,
  showTechnicalDetails = true,
}: {
  projectId: Ulid | null;
  showTechnicalDetails?: boolean;
}) {
  const { t } = useTranslation();
  const v3Context = useV3ProjectContext(projectId);
  const panel = useWorkflowPanel(projectId);

  if (v3Context.data?.currentLifecycleState) {
    return (
      <V3LifecyclePanel
        lifecycle={v3Context.data.currentLifecycleState}
        acceptanceCriteriaCount={v3Context.data.acceptanceCriteria.length}
        openQuestionsCount={v3Context.data.openQuestions.length}
      />
    );
  }

  if (panel.isPending || v3Context.isLoading) {
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
    if (!showTechnicalDetails) {
      return (
        <div className="flex flex-col items-start gap-2">
          <p className="text-sm font-medium">{t('chat.projectPanel.notStartedTitle')}</p>
          <p className="text-sm text-foreground-muted">{t('chat.projectPanel.noProgress')}</p>
        </div>
      );
    }

    // RN-02: com vínculo mas sem run, o estado normal é "execução ainda não iniciada" — não pedimos
    // para vincular um workflow (o sistema já atribuiu o padrão). O texto de "sem vínculo" vira
    // orientação de exceção, mostrado só quando, atipicamente, nenhum workflow está vinculado.
    const scope = panel.hasBinding ? 'notStarted' : 'empty';
    return (
      <div className="flex flex-col items-start gap-3">
        <p className="text-sm font-medium">{t(`chat.workflowPanel.${scope}.title`)}</p>
        <p className="text-sm text-foreground-muted">{t(`chat.workflowPanel.${scope}.body`)}</p>
        <Button asChild variant="outline" size="sm">
          <Link to="/workflows">{t(`chat.workflowPanel.${scope}.cta`)}</Link>
        </Button>
      </div>
    );
  }

  if (!showTechnicalDetails) {
    return (
      <BusinessProjectSummary
        phases={panel.phases}
        documents={panel.documents}
        tasks={panel.tasks}
        approvals={panel.approvals}
      />
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
          tasks={panel.tasks}
          approvals={panel.approvals}
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
  showTechnicalDetails = true,
}: {
  projectId: Ulid | null;
  onClose: () => void;
  showTechnicalDetails?: boolean;
}) {
  const { t } = useTranslation();
  const title = t(
    showTechnicalDetails ? 'chat.workflowPanel.title' : 'chat.projectPanel.title',
  );
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
        aria-label={title}
        tabIndex={-1}
        className="absolute right-0 top-0 flex h-full w-full max-w-sm flex-col gap-3 overflow-y-auto border-l border-border bg-background p-4 shadow-2xl"
      >
        <div className="flex items-center gap-2">
          <h2 className="flex-1 font-heading text-lg font-semibold">
            {title}
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
        <WorkflowPanel
          projectId={projectId}
          showTechnicalDetails={showTechnicalDetails}
        />
      </div>
    </div>
  );
}
