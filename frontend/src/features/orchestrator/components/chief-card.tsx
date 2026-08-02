import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Info, UserRoundPen } from 'lucide-react';

import type {
  Account,
  Agent,
  AgentDefinition,
  Budget,
  ChiefTurnState,
  Model,
  OperationMode,
  Project,
} from '@/api';
import { usePresentationMode } from '@/app/presentation';
import {
  Badge,
  Button,
  Card,
  CardContent,
  CardFooter,
  CardHeader,
  CardTitle,
  Tooltip,
  type BadgeProps,
} from '@/design-system';
import { formatCurrencyUSD, formatDateTime, formatNumber, formatRelativeTime } from '@/lib/format';
import { agentStateVariant, chiefTurnStateVariant, operationModeVariant } from '@/lib/status';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { SimulatedModeBadge } from '@/features/shared/components/simulated-mode-badge';
import { DrainDialog } from '@/features/orchestrator/components/drain-dialog';
import { HandoffWizard } from '@/features/orchestrator/components/handoff-wizard';
import {
  deriveChiefHealth,
  derivePresentedChiefHealth,
  deriveChiefReadiness,
  readinessAction,
  type BindingSource,
  type ChiefHealth,
  type ChiefReadiness,
} from '@/features/orchestrator/lib/orchestrator-derive';
import { publicLeadershipText } from '@/features/chat/lib/public-leadership';
import { usePauseChief, useResumeChief } from '@/features/orchestrator/hooks/use-orchestrator';
import { LeadershipProfileDialog } from '@/features/orchestrator/components/leadership-profile-dialog';
import { useLeadershipProfile } from '@/features/shared/hooks/use-leadership-profile';
import { ManagedAgentAvatar } from '@/features/shared/components/managed-agent-avatar';

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
  definition: AgentDefinition | null;
  model: Model | null;
  account: Account | null;
  models: Model[];
  accounts: Account[];
  /** Budgets já filtrados (escopo projeto e/ou conta) via `chiefBudgets`. */
  budgets: Budget[];
  turnState: ChiefTurnState | null;
  now: Date;
  /** Workflow vinculado ao projeto — pré-requisito de prontidão (§15). */
  hasWorkflow: boolean;
  /** Há tentativa em execução do chefe agora. */
  isRunning: boolean;
  /** Origem do vínculo do modelo (instância vs padrão da definição). */
  modelBinding: BindingSource;
  /**
   * Modo de operação efetivo (fonte da verdade: modo do workflow vinculado),
   * já resolvido pela página — ver `resolveOperationMode`.
   */
  operationMode: OperationMode;
  /**
   * Última atividade real (máximo entre atividade do projeto e a última
   * mensagem das conversas do chefe), já resolvida — ver `resolveLastActivityAt`.
   */
  lastActivityAt: string;
}

/** Linha rótulo/valor da ficha do chefe (definição, mobile-first). */
function InfoRow({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2">
      <dt className="text-sm text-foreground-muted">{label}</dt>
      <dd className="text-sm font-medium">{children}</dd>
    </div>
  );
}

/**
 * Rótulo com dica: texto do diagnóstico + botão de ajuda que abre um tooltip
 * explicando, em pt-BR simples, um conceito técnico (fencing/lease). O balão é
 * `aria-hidden`; o botão carrega o mesmo texto em `aria-label` para leitores.
 */
function DiagnosticLabel({ text, hint }: { text: string; hint: string }) {
  return (
    <span className="flex items-center gap-1">
      {text}
      <Tooltip label={hint}>
        <button
          type="button"
          aria-label={hint}
          className="rounded-full text-foreground-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <Info aria-hidden="true" className="size-3.5" />
        </button>
      </Tooltip>
    </span>
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
  definition,
  model,
  account,
  models,
  accounts,
  budgets,
  turnState,
  now,
  hasWorkflow,
  isRunning,
  modelBinding,
  operationMode,
  lastActivityAt,
}: ChiefCardProps) {
  const { t } = useTranslation();
  const { showTechnicalDetails } = usePresentationMode();
  const pauseMutation = usePauseChief(project.id);
  const resumeMutation = useResumeChief(project.id);
  const [dialog, setDialog] = useState<'drain' | 'handoff' | 'profile' | null>(null);
  const leadershipProfile = useLeadershipProfile();

  const chiefIdentity = resolveAgentIdentity('chief-orchestrator', chief.name);
  const publicName = leadershipProfile.data?.displayName ?? chiefIdentity.humanName;
  // A ficha é persistida e editável: uma instância antiga ainda carrega "Operações de IA" no
  // cargo. A humanização é de apresentação — o formulário de edição segue mostrando o texto real.
  const publicTitle = publicLeadershipText(
    leadershipProfile.data?.title ?? 'Diretora de Engenharia',
  );
  const processHealth = deriveChiefHealth(chief.state, chief.lastHeartbeatAt, now);
  const readiness = deriveChiefReadiness({
    model,
    account,
    hasWorkflow,
    agentState: chief.state,
    isRunning,
    health: processHealth,
  });
  const health = derivePresentedChiefHealth(processHealth, readiness);
  const action = readinessAction(readiness);
  const controlMutation = project.state === 'paused' ? resumeMutation : pauseMutation;
  const healthReason =
    processHealth === 'ok' && health === 'attention'
      ? t('orchestrator.chief.diagnostics.healthReadiness', {
          state: t(`orchestrator.readiness.states.${readiness}`),
        })
      : health === 'ok'
        ? t('orchestrator.chief.diagnostics.healthOk')
        : chief.state === 'error' || chief.state === 'outOfQuota'
          ? t('orchestrator.chief.diagnostics.healthState', {
              state: t(`status.agentState.${chief.state}`),
            })
          : chief.lastHeartbeatAt === null
            ? t('orchestrator.chief.diagnostics.healthMissingHeartbeat')
            : t('orchestrator.chief.diagnostics.healthStaleHeartbeat', {
                time: formatRelativeTime(chief.lastHeartbeatAt, undefined, now),
              });

  return (
    <Card>
      <CardHeader className="gap-2">
        <div className="flex flex-wrap items-center gap-2 pr-8">
          <ManagedAgentAvatar
            alias="chief-orchestrator"
            fallbackName={publicName}
            roleLabel={publicTitle}
            size={64}
            className="ring-2 ring-brand/35 ring-offset-2 ring-offset-background"
          />
          <div className="flex min-w-0 flex-col">
            <CardTitle>{publicName}</CardTitle>
            <span className="text-xs text-foreground-muted">{publicTitle}</span>
          </div>
          <Badge variant={agentStateVariant(chief.state)}>
            {t(`status.agentState.${chief.state}`)}
          </Badge>
          <span className="text-xs text-foreground-muted">
            {t(`status.agentStateHint.${chief.state}`)}
          </span>
          <Badge variant={turnState === null ? 'outline' : chiefTurnStateVariant(turnState)}>
            {turnState === null
              ? t('status.chiefTurnState.notStarted')
              : t(`status.chiefTurnState.${turnState}`)}
          </Badge>
        </div>
        <p className="text-xs text-foreground-muted">
          {t('orchestrator.chief.lastActivity', {
            time: formatRelativeTime(lastActivityAt, undefined, now),
          })}
        </p>
        <Button
          type="button"
          size="sm"
          variant="outline"
          className="min-h-11 self-start border-brand/50 bg-brand/10 font-semibold text-foreground shadow-sm hover:bg-brand/20"
          onClick={() => setDialog('profile')}
        >
          <UserRoundPen aria-hidden="true" className="size-4" />
          {t('orchestrator.profile.edit')}
        </Button>
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
          {/* Saúde é leitura de processo (heartbeat, lease): o dono não opera isso. */}
          {showTechnicalDetails ? (
            <InfoRow label={t('orchestrator.chief.health')}>
              <Badge variant={HEALTH_VARIANTS[health]}>{t(`orchestrator.health.${health}`)}</Badge>
            </InfoRow>
          ) : null}
          <InfoRow label={t('orchestrator.profile.communicationTitle')}>
            <span className="max-w-xl text-right text-xs font-normal text-foreground-muted">
              {leadershipProfile.data?.communicationInstructions ??
                t('orchestrator.profile.notConfigured')}
            </span>
          </InfoRow>
          <InfoRow label={t('orchestrator.chief.operationMode')}>
            <Badge variant={operationModeVariant(operationMode)}>
              {t(`status.operationMode.${operationMode}`)}
            </Badge>
          </InfoRow>
          {/* Modo de trabalho, conta e jornada por baixo: internals de assinatura
              e de modelo nunca aparecem para o cliente leigo (§2). */}
          {showTechnicalDetails ? (
            <>
              <InfoRow label={t('orchestrator.chief.model')}>
                {model ? (
                  <span className="flex flex-wrap items-center justify-end gap-2">
                    <span>{model.displayName}</span>
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
            </>
          ) : null}
        </dl>

        {showTechnicalDetails ? (
          <details className="rounded-md border border-border bg-surface-elevated p-3">
            <summary className="cursor-pointer text-sm font-medium">
              {t('orchestrator.chief.diagnostics.toggle')}
            </summary>
            <dl className="mt-2 flex flex-col gap-2">
              <InfoRow label={t('orchestrator.chief.diagnostics.healthReason')}>
                <span className="max-w-sm text-right font-normal">{healthReason}</span>
              </InfoRow>
              {chief.lease ? (
                <>
                  <InfoRow
                    label={
                      <DiagnosticLabel
                        text={t('orchestrator.chief.diagnostics.fencingToken')}
                        hint={t('orchestrator.chief.diagnostics.fencingTokenHint')}
                      />
                    }
                  >
                    {formatNumber(chief.lease.fencingToken)}
                  </InfoRow>
                  <InfoRow
                    label={
                      <DiagnosticLabel
                        text={t('orchestrator.chief.diagnostics.leaseExpiresAt')}
                        hint={t('orchestrator.chief.diagnostics.leaseHint')}
                      />
                    }
                  >
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
        ) : null}

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
      {dialog === 'profile' && definition ? (
        <LeadershipProfileDialog
          definition={definition}
          models={models}
          accounts={accounts}
          onClose={() => setDialog(null)}
        />
      ) : null}
    </Card>
  );
}
