import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import type { Account, Agent, Budget, ChiefTurnState, Model, Project } from '@/api';
import { Badge, Button, Card, CardContent, CardFooter, CardHeader, CardTitle, type BadgeProps } from '@/design-system';
import { formatCurrencyUSD, formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { agentStateVariant, chiefTurnStateVariant, operationModeVariant } from '@/lib/status';
import { SimulatedModeBadge } from '@/features/shared/components/simulated-mode-badge';
import { DrainDialog } from '@/features/orchestrator/components/drain-dialog';
import { HandoffWizard } from '@/features/orchestrator/components/handoff-wizard';
import {
  deriveChiefHealth,
  deriveChiefReadiness,
  readinessAction,
  type BindingSource,
  type ChiefHealth,
  type ChiefReadiness,
} from '@/features/orchestrator/lib/orchestrator-derive';
import { usePauseChief, useResumeChief } from '@/features/orchestrator/hooks/use-orchestrator';

/** Saúde derivada (conceito local da feature) → variante semântica do Badge. */
const HEALTH_VARIANTS: Record<ChiefHealth, BadgeProps['variant']> = {
  ok: 'success',
  attention: 'warning',
  error: 'error',
};

/** Prontidão operacional → variante do Badge (§15). */
const READINESS_VARIANTS: Record<ChiefReadiness, BadgeProps['variant']> = {
  notConfigured: 'outline',
  awaitingProvider: 'warning',
  awaitingWorkflow: 'warning',
  ready: 'success',
  running: 'info',
  degraded: 'error',
};

/** Rota da CTA de cada estado de prontidão. */
const READINESS_ROUTES = {
  configureProvider: '/providers',
  chooseModel: '/providers?tab=models',
  linkWorkflow: '/workflows',
  reviewAgents: '/agents',
} as const;

export interface ChiefCardProps {
  project: Project;
  chief: Agent;
  model: Model | null;
  account: Account | null;
  /** Budgets já filtrados (escopo projeto e/ou conta) via `chiefBudgets`. */
  budgets: Budget[];
  turnState: ChiefTurnState;
  now: Date;
  /** Workflow vinculado ao projeto — pré-requisito de prontidão (§15). */
  hasWorkflow: boolean;
  /** Há tentativa em execução do chefe agora. */
  isRunning: boolean;
  /** Origem do vínculo do modelo (instância vs padrão da definição). */
  modelBinding: BindingSource;
}

/** Linha rótulo/valor da ficha do chefe (definição, mobile-first). */
function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2">
      <dt className="text-sm text-foreground-muted">{label}</dt>
      <dd className="text-sm font-medium">{children}</dd>
    </div>
  );
}

/**
 * Card do chefe do projeto: identidade, estado, turno (realtime), saúde
 * derivada, modo de operação, modelo/conta/cota em uso e as ações de
 * orquestração (pausar/retomar, drenar, passagem de bastão). Lease/fencing
 * fica na seção de diagnóstico avançado (colapsada).
 */
export function ChiefCard({
  project,
  chief,
  model,
  account,
  budgets,
  turnState,
  now,
  hasWorkflow,
  isRunning,
  modelBinding,
}: ChiefCardProps) {
  const { t } = useTranslation();
  const pauseMutation = usePauseChief(project.id);
  const resumeMutation = useResumeChief(project.id);
  const [dialog, setDialog] = useState<'drain' | 'handoff' | null>(null);

  const health = deriveChiefHealth(chief.state, chief.lastHeartbeatAt, now);
  const readiness = deriveChiefReadiness({
    model,
    account,
    hasWorkflow,
    agentState: chief.state,
    isRunning,
    health,
  });
  const action = readinessAction(readiness);
  const controlMutation = project.state === 'paused' ? resumeMutation : pauseMutation;

  return (
    <Card>
      <CardHeader className="gap-2">
        <div className="flex flex-wrap items-center gap-2 pr-8">
          <CardTitle>{chief.name}</CardTitle>
          <Badge variant={agentStateVariant(chief.state)}>
            {t(`status.agentState.${chief.state}`)}
          </Badge>
          <Badge variant={chiefTurnStateVariant(turnState)}>
            {t(`status.chiefTurnState.${turnState}`)}
          </Badge>
        </div>
        <p className="text-xs text-foreground-muted">
          {t('orchestrator.chief.lastActivity', {
            time: formatRelativeTime(project.lastActivityAt, undefined, now),
          })}
        </p>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {/* Prontidão real: nunca apresentamos "pronto" sem dependências (§15). */}
        <div className="flex flex-col gap-2 rounded-md border border-border bg-surface-elevated p-3">
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant={READINESS_VARIANTS[readiness]}>
              {t(`orchestrator.readiness.states.${readiness}`)}
            </Badge>
            <SimulatedModeBadge />
          </div>
          <p className="text-xs text-foreground-muted">
            {t(`orchestrator.readiness.explanations.${readiness}`)}
          </p>
          {action ? (
            <Button asChild size="sm" variant="outline" className="self-start">
              <Link to={READINESS_ROUTES[action]}>
                {t(`orchestrator.readiness.actions.${action}`)}
              </Link>
            </Button>
          ) : null}
        </div>

        <dl className="flex flex-col gap-2">
          <InfoRow label={t('orchestrator.chief.health')}>
            <Badge variant={HEALTH_VARIANTS[health]}>{t(`orchestrator.health.${health}`)}</Badge>
          </InfoRow>
          <InfoRow label={t('orchestrator.chief.operationMode')}>
            <Badge variant={operationModeVariant(project.operationMode)}>
              {t(`status.operationMode.${project.operationMode}`)}
            </Badge>
          </InfoRow>
          <InfoRow label={t('orchestrator.chief.model')}>
            {model ? (
              <span className="flex flex-wrap items-center justify-end gap-2">
                {model.displayName}
                {/* Padrão da definição ainda não exercido pela instância não é
                    "em uso": rotulamos como binding pendente (§15/§17). */}
                {modelBinding === 'definitionDefault' ? (
                  <Badge variant="outline">{t('orchestrator.chief.bindingPending')}</Badge>
                ) : null}
              </span>
            ) : (
              t('orchestrator.chief.noModel')
            )}
          </InfoRow>
          <InfoRow label={t('orchestrator.chief.account')}>
            {account ? account.label : t('orchestrator.chief.noAccount')}
          </InfoRow>
          <InfoRow label={t('orchestrator.chief.quota')}>
            {budgets.length === 0 ? (
              t('orchestrator.chief.noBudget')
            ) : (
              <span className="flex flex-col items-end gap-1">
                {budgets.map((budget) => (
                  <span key={budget.id}>
                    {t(`status.budgetScope.${budget.scope}`)} ·{' '}
                    {t(`status.budgetPeriod.${budget.period}`)}:{' '}
                    {t('orchestrator.chief.quotaUsage', {
                      used: formatCurrencyUSD(budget.spentUsd),
                      limit: formatCurrencyUSD(budget.limitUsd),
                    })}
                  </span>
                ))}
              </span>
            )}
          </InfoRow>
        </dl>

        <details className="rounded-md border border-border bg-surface-elevated p-3">
          <summary className="cursor-pointer text-sm font-medium">
            {t('orchestrator.chief.diagnostics.toggle')}
          </summary>
          <dl className="mt-2 flex flex-col gap-2">
            {chief.lease ? (
              <>
                <InfoRow label={t('orchestrator.chief.diagnostics.fencingToken')}>
                  {formatNumber(chief.lease.fencingToken)}
                </InfoRow>
                <InfoRow label={t('orchestrator.chief.diagnostics.leaseExpiresAt')}>
                  {formatDateTime(chief.lease.expiresAt)}
                </InfoRow>
              </>
            ) : (
              <p className="text-sm text-foreground-muted">
                {t('orchestrator.chief.diagnostics.noLease')}
              </p>
            )}
          </dl>
        </details>

        {controlMutation.isError ? (
          <p role="alert" className="text-sm text-error">
            {t('orchestrator.mutation.error')}
          </p>
        ) : null}
      </CardContent>
      <CardFooter className="flex flex-wrap gap-2">
        {project.state === 'active' ? (
          <Button
            type="button"
            variant="outline"
            disabled={pauseMutation.isPending}
            onClick={() => pauseMutation.mutate()}
          >
            {t('orchestrator.actions.pause')}
          </Button>
        ) : null}
        {project.state === 'paused' ? (
          <Button
            type="button"
            variant="outline"
            disabled={resumeMutation.isPending}
            onClick={() => resumeMutation.mutate()}
          >
            {t('orchestrator.actions.resume')}
          </Button>
        ) : null}
        <Button type="button" variant="secondary" onClick={() => setDialog('drain')}>
          {t('orchestrator.actions.drain')}
        </Button>
        <Button type="button" onClick={() => setDialog('handoff')}>
          {t('orchestrator.actions.handoff')}
        </Button>
      </CardFooter>

      {dialog === 'drain' ? (
        <DrainDialog projectId={project.id} onClose={() => setDialog(null)} />
      ) : null}
      {dialog === 'handoff' ? (
        <HandoffWizard
          projectId={project.id}
          currentModel={model}
          onClose={() => setDialog(null)}
        />
      ) : null}
    </Card>
  );
}
