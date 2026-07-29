import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Check, Copy, X } from 'lucide-react';

import type { Agent, Ulid } from '@/api';
import { Badge, Button, Field, Select, Skeleton } from '@/design-system';
import { formatCurrencyUSD, formatDateTime, formatDurationMs, formatNumber, formatRelativeTime } from '@/lib/format';
import { attemptStateVariant, priorityVariant, taskStateVariant } from '@/lib/status';
import { cn } from '@/lib/utils';
import { TaskActions } from '@/features/board/components/task-actions';
import { TaskApprovals } from '@/features/board/components/task-approvals';
import { useTaskDetail, useTaskRealtime } from '@/features/board/hooks/use-board';
import {
  resolveBoardPresentation,
  taskStateLabelKey,
  unassignedLabelKey,
} from '@/features/board/lib/board-presentation';
import { ProgressTracks } from '@/features/cockpit/components/progress-tracks';
import { progressEvidence } from '@/features/cockpit/lib/cockpit-derive';

export interface TaskDetailProps {
  taskId: Ulid;
  agents: Agent[];
  showTechnicalDetails?: boolean;
  /** Quando definido (drawer/página), renderiza o botão de fechar. */
  onClose?: () => void;
}

const BUSINESS_PROGRESS = [
  { key: 'executed', fill: 'bg-brand' },
  { key: 'validated', fill: 'bg-info' },
  { key: 'approved', fill: 'bg-success' },
] as const;

/**
 * Detalhe da tarefa — mesmo conteúdo no drawer (desktop) e na página
 * dedicada (mobile): progresso em 3 trilhas, instrução imutável e
 * versionada, tentativas com evidências (custo/tokens/duração/commits/
 * timeline), demanda de origem, gates e ações humanas.
 */
export function TaskDetail({
  taskId,
  agents,
  showTechnicalDetails = false,
  onClose,
}: TaskDetailProps) {
  const { t, i18n } = useTranslation();
  useTaskRealtime(taskId);
  const { task, instructions, attempts, attemptEvents, approvals, demand, isPending, isError, refetch } =
    useTaskDetail(taskId);

  const [selectedVersion, setSelectedVersion] = useState<number | null>(null);
  const [copied, setCopied] = useState(false);
  const agentNames = useMemo(
    () => new Map(agents.map((agent) => [agent.id, agent.name])),
    [agents],
  );
  const presentation = resolveBoardPresentation(showTechnicalDetails);

  async function copyId() {
    if (!task) return;
    try {
      await navigator.clipboard.writeText(task.id);
      setCopied(true);
      setTimeout(() => setCopied(false), 2_000);
    } catch {
      // Ambiente sem clipboard (ex.: contexto não seguro): silencioso —
      // o ID completo continua visível e selecionável no `<code>`.
    }
  }

  if (isPending) {
    return (
      <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
        <Skeleton className="h-8 w-2/3" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-40 w-full" />
      </div>
    );
  }

  if (isError || !task) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p role="alert" className="text-sm text-error">
          {t('common.states.errorBody')}
        </p>
        <Button type="button" variant="outline" onClick={refetch}>
          {t('common.actions.retry')}
        </Button>
      </div>
    );
  }

  const currentVersion = selectedVersion ?? task.instructionVersion;
  const instruction =
    instructions.find((entry) => entry.version === currentVersion) ?? instructions[0] ?? null;
  const assignedName = task.assigneeAgentId
    ? agentNames.get(task.assigneeAgentId)?.split(/\s+[—–]\s+/u)[0]?.trim()
    : null;

  return (
    <div className="flex flex-col gap-5">
      <header
        className={cn(
          'flex min-w-0 items-start justify-between gap-3',
          onClose &&
            'sticky -top-4 z-10 -mx-4 -mt-4 border-b border-border bg-background px-4 pb-3 pt-4',
        )}
      >
        <div className="flex min-w-0 flex-1 flex-col gap-2">
          <h2 className="break-words font-heading text-xl font-semibold leading-snug">
            {task.title}
          </h2>
          <div className="flex flex-wrap items-center gap-1.5">
            <Badge variant={taskStateVariant(task.state)}>
              {t(taskStateLabelKey(task.state, showTechnicalDetails))}
            </Badge>
            <Badge variant={priorityVariant(task.priority)}>
              {showTechnicalDetails
                ? t(`status.priority.${task.priority}`)
                : t('board.card.priority', {
                    priority: t(`status.priority.${task.priority}`).toLocaleLowerCase(),
                  })}
            </Badge>
            {presentation.showCardType && (
              <Badge
                variant={
                  task.cardType === 'human_gate'
                    ? 'warning'
                    : task.cardType === 'decision'
                      ? 'brand'
                      : 'info'
                }
              >
                {t(`board.card.types.${task.cardType ?? 'agent_task'}`)}
              </Badge>
            )}
            {task.phaseName && <Badge variant="brand">{task.phaseName}</Badge>}
            {task.archivedAt !== null && (
              <Badge variant="info">{t('board.card.archived')}</Badge>
            )}
          </div>
          <dl className="grid gap-2 text-xs text-foreground-muted md:grid-cols-2">
            {presentation.showInternalId && (
              <div className="flex items-center gap-1.5 md:col-span-2">
                <dt>{t('board.detail.id')}:</dt>
                <dd className="flex min-w-0 items-center gap-1.5">
                  <code className="min-w-0 break-all rounded bg-surface-elevated px-1.5 py-0.5 font-mono text-[0.7rem] text-foreground">
                    {task.id}
                  </code>
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    className="size-6 shrink-0"
                    aria-label={copied ? t('board.detail.copiedId') : t('board.detail.copyId')}
                    onClick={() => void copyId()}
                  >
                    {copied ? (
                      <Check aria-hidden="true" className="size-3.5 text-success" />
                    ) : (
                      <Copy aria-hidden="true" className="size-3.5" />
                    )}
                  </Button>
                </dd>
              </div>
            )}
            <div className="flex min-w-0 gap-1">
              <dt>{t('board.detail.assignee')}:</dt>
              <dd className="min-w-0 truncate">
                {assignedName ?? t(unassignedLabelKey(showTechnicalDetails))}
              </dd>
            </div>
            <div className="flex gap-1">
              <dt>{t('board.detail.updated')}:</dt>
              <dd>{formatRelativeTime(task.updatedAt, i18n.language)}</dd>
            </div>
          </dl>
          {task.state === 'blocked' && task.blockedReason && (
            <p className="text-sm text-error">
              {t(showTechnicalDetails ? 'board.card.blocked' : 'board.card.blockedBusiness', {
                reason: task.blockedReason,
              })}
            </p>
          )}
        </div>
        {onClose && (
          <Button type="button" variant="ghost" size="icon" onClick={onClose} aria-label={t('board.detail.close')}>
            <X aria-hidden="true" />
          </Button>
        )}
      </header>

      <section aria-labelledby="task-scope" className="flex flex-col gap-2">
        <h3 id="task-scope" className="font-heading text-sm font-semibold">
          {t(
            showTechnicalDetails
              ? 'board.detail.scope.title'
              : 'board.detail.scope.businessTitle',
          )}
        </h3>
        <dl className="grid gap-3 rounded-lg border border-border bg-surface p-3 text-sm">
          <div>
            <dt className="text-xs font-medium text-foreground-muted">
              {t('board.detail.scope.objective')}
            </dt>
            <dd>
              {demand?.description ||
                t(
                  showTechnicalDetails
                    ? 'board.detail.sourceUnavailable'
                    : 'board.detail.objectiveUnavailable',
                )}
            </dd>
          </div>
          {showTechnicalDetails && (
            <>
              <div className="grid gap-3 md:grid-cols-2">
                <div>
                  <dt className="text-xs font-medium text-foreground-muted">
                    {t('board.detail.scope.included')}
                  </dt>
                  <dd className="text-foreground-muted">{t('board.detail.sourceUnavailable')}</dd>
                </div>
                <div>
                  <dt className="text-xs font-medium text-foreground-muted">
                    {t('board.detail.scope.excluded')}
                  </dt>
                  <dd className="text-foreground-muted">{t('board.detail.sourceUnavailable')}</dd>
                </div>
              </div>
              <div>
                <dt className="text-xs font-medium text-foreground-muted">
                  {t('board.detail.scope.acceptanceCriteria')}
                </dt>
                <dd className="text-foreground-muted">{t('board.detail.sourceUnavailable')}</dd>
              </div>
              <div>
                <dt className="text-xs font-medium text-foreground-muted">
                  {t('board.detail.scope.claims')}
                </dt>
                <dd className="text-foreground-muted">{t('board.detail.sourceUnavailable')}</dd>
              </div>
            </>
          )}
        </dl>
      </section>

      <section aria-labelledby="task-progress" className="flex flex-col gap-2">
        <h3 id="task-progress" className="font-heading text-sm font-semibold">
          {t('board.detail.progressTitle')}
        </h3>
        {showTechnicalDetails ? (
          <ProgressTracks progress={task.progress} evidence={progressEvidence([task])} />
        ) : (
          <div className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-3">
            <p className="text-xs text-foreground-muted">
              {t('board.detail.businessProgress.hint')}
            </p>
            {BUSINESS_PROGRESS.map(({ key, fill }) => (
              <div key={key} className="flex flex-col gap-1.5">
                <div className="flex items-center justify-between gap-3 text-sm">
                  <span className="text-foreground-muted">
                    {t(`board.detail.businessProgress.${key}`)}
                  </span>
                  <span className="font-medium tabular-nums">{task.progress[key]}%</span>
                </div>
                <div
                  role="progressbar"
                  aria-label={t(`board.detail.businessProgress.${key}`)}
                  aria-valuenow={task.progress[key]}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  className="h-2 overflow-hidden rounded-full bg-surface-elevated"
                >
                  <div
                    className={cn('h-full rounded-full transition-[width]', fill)}
                    style={{ width: `${task.progress[key]}%` }}
                  />
                </div>
              </div>
            ))}
          </div>
        )}
      </section>

      {showTechnicalDetails && (
        <>
      <section aria-labelledby="task-instruction" className="flex flex-col gap-2">
        <h3 id="task-instruction" className="font-heading text-sm font-semibold">
          {t('board.detail.instruction.title')}
        </h3>
        <p className="text-xs text-foreground-muted">{t('board.detail.instruction.immutable')}</p>
        {instructions.length > 1 && (
          <Field htmlFor="instruction-version" label={t('board.detail.instruction.versionLabel')}>
            <Select
              id="instruction-version"
              value={String(currentVersion)}
              onChange={(event) => setSelectedVersion(Number(event.target.value))}
            >
              {instructions.map((entry) => (
                <option key={entry.id} value={entry.version}>
                  {t('board.detail.instruction.version', { version: entry.version })}
                </option>
              ))}
            </Select>
          </Field>
        )}
        {instruction && (
          <div className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-3">
            <div className="flex items-center gap-2 text-xs text-foreground-muted">
              <Badge variant="outline">
                {t(`board.detail.instruction.author.${instruction.authorKind}`)}
              </Badge>
              <span>{formatDateTime(instruction.createdAt, i18n.language)}</span>
            </div>
            <p className="whitespace-pre-wrap text-sm">{instruction.body}</p>
          </div>
        )}
      </section>

      <section aria-labelledby="task-attempts" className="flex flex-col gap-2">
        <h3 id="task-attempts" className="font-heading text-sm font-semibold">
          {t('board.detail.attempts.title')}
        </h3>
        {attempts.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('board.detail.attempts.empty')}</p>
        ) : (
          <ol className="flex flex-col gap-3">
            {attempts.map((attempt) => {
              const events = attemptEvents.filter((event) => event.attemptId === attempt.id);
              return (
                <li
                  key={attempt.id}
                  className="flex flex-col gap-2 rounded-lg border border-border bg-surface p-3"
                >
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-medium">
                      {t('board.detail.attempts.attempt', { number: attempt.number })}
                    </span>
                    <Badge variant={attemptStateVariant(attempt.state)}>
                      {t(`status.attemptState.${attempt.state}`)}
                    </Badge>
                    <span className="text-xs text-foreground-muted">
                      {agentNames.get(attempt.agentId) ?? t('board.card.unassigned')}
                    </span>
                  </div>
                  <dl className="grid grid-cols-2 gap-2 text-xs md:grid-cols-4">
                    <div>
                      <dt className="text-foreground-muted">{t('board.detail.attempts.duration')}</dt>
                      <dd className="tabular-nums">
                        {attempt.durationMs !== null
                          ? formatDurationMs(attempt.durationMs)
                          : t('board.detail.attempts.running')}
                      </dd>
                    </div>
                    <div>
                      <dt className="text-foreground-muted">{t('board.detail.attempts.cost')}</dt>
                      <dd className="tabular-nums">{formatCurrencyUSD(attempt.costUsd, i18n.language)}</dd>
                    </div>
                    <div className="col-span-2">
                      <dt className="text-foreground-muted">{t('board.detail.attempts.tokens')}</dt>
                      <dd className="tabular-nums">
                        {formatNumber(attempt.tokensInput, {}, i18n.language)} /{' '}
                        {formatNumber(attempt.tokensOutput, {}, i18n.language)}
                      </dd>
                    </div>
                  </dl>
                  {attempt.summary && <p className="text-sm">{attempt.summary}</p>}
                  {attempt.failureReason && (
                    <p className="text-sm text-error">{attempt.failureReason}</p>
                  )}
                  {attempt.commitRefs.length > 0 && (
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="text-xs text-foreground-muted">
                        {t('board.detail.attempts.commits')}:
                      </span>
                      {attempt.commitRefs.map((ref) => (
                        <code
                          key={ref}
                          className="rounded bg-surface-elevated px-1.5 py-0.5 font-mono text-xs"
                        >
                          {ref}
                        </code>
                      ))}
                    </div>
                  )}
                  {events.length > 0 && (
                    <ul className="flex flex-col gap-1 border-l-2 border-border pl-3">
                      {events.map((event) => (
                        <li key={event.id} className="flex items-baseline gap-2 text-xs">
                          <Badge variant="outline">{t(`board.detail.eventKinds.${event.kind}`)}</Badge>
                          <span className="flex-1">{event.content}</span>
                          <span className="shrink-0 text-foreground-muted">
                            {formatDateTime(event.occurredAt, i18n.language)}
                          </span>
                        </li>
                      ))}
                    </ul>
                  )}
                </li>
              );
            })}
          </ol>
        )}
      </section>

      <section aria-labelledby="task-demand" className="flex flex-col gap-2">
        <h3 id="task-demand" className="font-heading text-sm font-semibold">
          {t('board.detail.demand.title')}
        </h3>
        {demand ? (
          <div className="rounded-lg border border-border bg-surface p-3">
            <p className="text-sm font-medium">{demand.title}</p>
            <p className="text-xs text-foreground-muted">{demand.description}</p>
          </div>
        ) : (
          <p className="text-sm text-foreground-muted">{t('board.detail.demand.none')}</p>
        )}
      </section>
        </>
      )}

      <section aria-labelledby="task-approvals" className="flex flex-col gap-2">
        <h3 id="task-approvals" className="font-heading text-sm font-semibold">
          {t(
            showTechnicalDetails
              ? 'board.detail.approvals.title'
              : 'board.detail.approvals.businessTitle',
          )}
        </h3>
        <TaskApprovals
          approvals={approvals}
          showTechnicalDetails={showTechnicalDetails}
        />
      </section>

      <section aria-labelledby="task-actions" className="flex flex-col gap-2">
        <h3 id="task-actions" className="font-heading text-sm font-semibold">
          {t(
            showTechnicalDetails
              ? 'board.detail.actions.title'
              : 'board.detail.actions.businessTitle',
          )}
        </h3>
        <TaskActions task={task} showTechnicalDetails={showTechnicalDetails} />
      </section>
    </div>
  );
}
