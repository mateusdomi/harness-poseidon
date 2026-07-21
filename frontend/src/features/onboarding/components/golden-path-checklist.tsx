import {
  ArrowRight,
  Bot,
  Building2,
  CheckCircle2,
  Circle,
  Cpu,
  FolderKanban,
  Lock,
  Play,
  Rocket,
  ScrollText,
  UserRoundCheck,
  Users,
  Workflow,
  type LucideIcon,
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { useGoldenPath } from '@/features/onboarding/hooks/use-golden-path';
import type { ReadinessStep } from '@/api';
import type { StepStatus } from '@/features/onboarding/lib/golden-path';
import { cn } from '@/lib/utils';

const STEP_ICONS: Record<ReadinessStep, LucideIcon> = {
  ProfileReady: UserRoundCheck,
  OrganizationReady: Building2,
  ProjectReady: FolderKanban,
  ProviderAccountReady: ScrollText,
  ModelReady: Cpu,
  WorkflowReady: Workflow,
  ChiefDefinitionReady: Bot,
  AgentPoolReady: Users,
  ExecutionReady: Play,
};

/**
 * Rota da CTA: o backend publica a rota canônica de cada ação. Enriquecemos
 * apenas os deep links que preservam intenção — criar organização voltando ao
 * fluxo de projeto, e criar projeto com a organização pré-selecionada.
 */
function actionRoute(
  action: { code: string; route: string; resourceId: string | null } | null,
  organizationId: string | null,
): string | null {
  if (!action) return null;
  if (action.code === 'organization.create') return `${action.route}?new=1&return=project`;
  if (action.code === 'project.create') {
    return organizationId
      ? `${action.route}?new=1&org=${organizationId}`
      : `${action.route}?new=1`;
  }
  if (action.code === 'conversation.start') return '/chat';
  return action.route;
}

const STATUS_ICON: Record<StepStatus, LucideIcon> = {
  done: CheckCircle2,
  current: ArrowRight,
  blocked: Lock,
  pending: Circle,
};

const STATUS_ICON_CLASS: Record<StepStatus, string> = {
  done: 'text-success',
  current: 'text-brand-strong',
  blocked: 'text-foreground-muted',
  pending: 'text-foreground-muted',
};

/**
 * Checklist persistente do golden path. Cada etapa expõe status, explicação,
 * CTA único e bloqueador — derivados de `useGoldenPath` (recursos reais).
 * Não duplica a fonte da verdade: apenas apresenta o estado derivado.
 */
export interface GoldenPathChecklistProps {
  className?: string;
  /** Oculta o card quando o caminho já está completo (uso no Cockpit). */
  hideWhenComplete?: boolean;
}

export function GoldenPathChecklist({ className, hideWhenComplete = false }: GoldenPathChecklistProps) {
  const { t } = useTranslation();
  const { state, activeOrganizationId, isLoading, isError, refetch } = useGoldenPath();

  if (hideWhenComplete && !isLoading && !isError && state.complete) {
    return null;
  }

  if (isLoading) {
    return (
      <Card className={className}>
        <CardHeader>
          <CardTitle>{t('goldenPath.title')}</CardTitle>
        </CardHeader>
        <CardContent
          className="flex flex-col gap-3"
          role="status"
          aria-label={t('common.states.loading')}
        >
          <Skeleton className="h-4 w-40" />
          <Skeleton className="h-16 w-full" />
          <Skeleton className="h-16 w-full" />
        </CardContent>
      </Card>
    );
  }

  if (isError) {
    return (
      <Card className={className}>
        <CardHeader>
          <CardTitle>{t('goldenPath.title')}</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={refetch}>
            {t('common.actions.retry')}
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card className={className}>
      <CardHeader>
        {/* O aviso de modo simulado é responsabilidade da página que hospeda o
            checklist (ex.: cabeçalho do Cockpit) — não repetimos aqui. */}
        <div className="flex items-center gap-2">
          <Rocket aria-hidden="true" className="size-5 text-brand-strong" />
          <CardTitle>{t('goldenPath.title')}</CardTitle>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="flex flex-col gap-2">
          <p className="text-sm text-foreground-muted">
            {state.complete ? t('goldenPath.complete.body') : t('goldenPath.subtitle')}
          </p>
          <div className="flex items-center gap-3">
            <div
              className="h-2 flex-1 overflow-hidden rounded-full bg-surface-elevated"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={state.totalCount}
              aria-valuenow={state.doneCount}
              aria-label={t('goldenPath.progress', {
                done: state.doneCount,
                total: state.totalCount,
              })}
            >
              <div
                className="h-full rounded-full bg-primary bg-[image:var(--gradient-primary)] motion-safe:transition-[width] motion-safe:duration-base"
                style={{ width: `${(state.doneCount / state.totalCount) * 100}%` }}
              />
            </div>
            <span className="whitespace-nowrap text-xs font-medium text-foreground-muted">
              {t('goldenPath.progress', { done: state.doneCount, total: state.totalCount })}
            </span>
          </div>
        </div>

        {state.complete ? (
          <p className="flex items-center gap-2 rounded-md bg-success/10 px-3 py-2 text-sm text-foreground">
            <CheckCircle2 aria-hidden="true" className="size-4 shrink-0 text-success" />
            {t('goldenPath.complete.title')}
          </p>
        ) : null}

        <ol className="flex flex-col gap-2">
          {state.steps.map((step, index) => {
            const StepIcon = STEP_ICONS[step.id];
            const StatusIcon = STATUS_ICON[step.status];
            const isCurrent = step.status === 'current';
            // Rota e rótulo da CTA vêm do read model canônico; a organização
            // ativa enriquece o deep link de criação de projeto.
            const route = actionRoute(step.nextAction, activeOrganizationId);
            const blockerLabel =
              step.blockerCodes.length > 0
                ? step.blockerCodes
                    .map((code) => t(`goldenPath.blockers.${code}`, { defaultValue: code }))
                    .join(' · ')
                : null;

            return (
              <li
                key={step.id}
                className={cn(
                  'flex flex-col gap-2 rounded-lg border p-3 md:flex-row md:items-center',
                  isCurrent
                    ? 'border-brand-strong bg-brand/5 ring-1 ring-inset ring-brand-strong/30'
                    : 'border-border',
                )}
              >
                <span className="flex items-center gap-3">
                  <StatusIcon
                    aria-hidden="true"
                    className={cn('size-5 shrink-0', STATUS_ICON_CLASS[step.status])}
                  />
                  <span className="sr-only">{t(`goldenPath.status.${step.status}`)}</span>
                  <StepIcon aria-hidden="true" className="size-4 shrink-0 text-foreground-muted" />
                  <span className="flex flex-col">
                    <span className="flex flex-wrap items-center gap-2">
                      <span
                        className={cn(
                          'text-sm font-medium',
                          step.status === 'done' && 'text-foreground-muted line-through',
                        )}
                      >
                        {index + 1}. {t(`goldenPath.steps.${step.id}.title`)}
                      </span>
                      {/* Dependência que só funciona em modo simulado precisa
                          dizer isso — concluída não é o mesmo que real. */}
                      {step.executionMode === 'simulated' ? (
                        <Badge variant="warning">{t('common.simulated.label')}</Badge>
                      ) : null}
                    </span>
                    {isCurrent ? (
                      <span className="text-xs text-foreground-muted">
                        {t(step.messageCode, {
                          defaultValue: t(`goldenPath.steps.${step.id}.explanation`),
                        })}
                      </span>
                    ) : null}
                    {blockerLabel ? (
                      <span className="text-xs text-foreground-muted">{blockerLabel}</span>
                    ) : null}
                  </span>
                </span>

                {route && isCurrent ? (
                  <Button asChild size="sm" className="md:ml-auto">
                    <Link to={route}>
                      {t(`goldenPath.actions.${step.nextAction!.code}`, {
                        defaultValue: t(`goldenPath.steps.${step.id}.cta`),
                      })}
                      <ArrowRight aria-hidden="true" className="size-4" />
                    </Link>
                  </Button>
                ) : route && step.status === 'pending' ? (
                  <Button asChild variant="ghost" size="sm" className="md:ml-auto">
                    <Link to={route}>
                      {t(`goldenPath.actions.${step.nextAction!.code}`, {
                        defaultValue: t(`goldenPath.steps.${step.id}.cta`),
                      })}
                    </Link>
                  </Button>
                ) : null}
              </li>
            );
          })}
        </ol>
      </CardContent>
    </Card>
  );
}
