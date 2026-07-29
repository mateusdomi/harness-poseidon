import { useTranslation } from 'react-i18next';
import { Activity } from 'lucide-react';

import { Badge, Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { usePresentationMode } from '@/app/presentation';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { ActivityFeed } from '@/features/cockpit/components/activity-feed';
import {
  BlockedTasksCard,
  PendingApprovalsCard,
} from '@/features/cockpit/components/attention-cards';
import { QuotaCard, TeamCapacityCard } from '@/features/cockpit/components/health-cards';
import { FleetOverview } from '@/features/cockpit/components/fleet-overview';
import { NextActionCard } from '@/features/cockpit/components/next-action-card';
import { GovernanceHealthCard } from '@/features/cockpit/components/governance-health-card';
import { WorkflowProgress } from '@/features/cockpit/components/workflow-progress';
import { GoldenPathChecklist } from '@/features/onboarding/components/golden-path-checklist';
import { SimulatedModeBadge } from '@/features/shared/components/simulated-mode-badge';
import { ProgressTracks } from '@/features/cockpit/components/progress-tracks';
import { ProjectTimeline } from '@/features/cockpit/components/project-timeline';
import { TasksByStateChart } from '@/features/cockpit/components/tasks-by-state-chart';
import {
  useCockpitActivity,
  useCockpitAgents,
  useCockpitApprovals,
  useCockpitBudgets,
  useCockpitPhaseProgress,
  useCockpitRealtime,
  useCockpitTasks,
  useCockpitWorkflow,
} from '@/features/cockpit/hooks/use-cockpit';
import {
  aggregateProgress,
  budgetSeverity,
  countTasksByState,
  currentPhase,
  progressEvidence,
  recommendNextAction,
} from '@/features/cockpit/lib/cockpit-derive';
import { featureFlags } from '@/config/features';

export default function CockpitPage() {
  const { t } = useTranslation();
  const presentation = usePresentationMode();
  const technical = presentation.showTechnicalDetails;
  const presentationMode = technical ? 'technical' : 'business';
  const { activeProject, isPending, isError, refetch } = useActiveProject();

  const projectId = activeProject?.id ?? null;
  useCockpitRealtime(projectId);

  const tasksQuery = useCockpitTasks(projectId);
  const approvalsQuery = useCockpitApprovals(projectId);
  const agentsQuery = useCockpitAgents(projectId);
  const budgetsQuery = useCockpitBudgets(technical);
  const activityQuery = useCockpitActivity();
  const workflowData = useCockpitWorkflow(projectId);
  const phaseProgress = useCockpitPhaseProgress(
    workflowData.run?.id ?? null,
    workflowData.phases,
  );

  const loading =
    isPending ||
    tasksQuery.isLoading ||
    approvalsQuery.isLoading ||
    agentsQuery.isLoading ||
    (technical && budgetsQuery.isLoading) ||
    activityQuery.isLoading ||
    workflowData.isPending ||
    phaseProgress.isPending;
  const errored =
    isError ||
    tasksQuery.isError ||
    approvalsQuery.isError ||
    agentsQuery.isError ||
    (technical && budgetsQuery.isError) ||
    activityQuery.isError ||
    workflowData.isError;

  function retryAll() {
    refetch();
    void tasksQuery.refetch();
    void approvalsQuery.refetch();
    void agentsQuery.refetch();
    if (technical) void budgetsQuery.refetch();
    void activityQuery.refetch();
    workflowData.refetch();
    phaseProgress.refetch();
  }

  const tasks = tasksQuery.data ?? [];
  const approvals = approvalsQuery.data ?? [];
  const agents = agentsQuery.data ?? [];
  const budgets = budgetsQuery.data ?? [];
  const events = activityQuery.data ?? [];
  const phase = currentPhase(workflowData.phases);
  const canonicalProgress = phase ? phaseProgress.byPhaseId.get(phase.id) ?? null : null;
  const humanApprovals = approvals.filter(
    (approval) =>
      approval.documentId !== null ||
      workflowData.gates.some(
        (gate) =>
          gate.id === approval.gateId &&
          gate.requiresApproval,
      ),
  );
  const humanGateCount = humanApprovals.filter(
    (approval) =>
      approval.state === 'pending' &&
      workflowData.gates.some((gate) => gate.id === approval.gateId && gate.phaseId === phase?.id),
  ).length;
  const counts = countTasksByState(tasks);
  const nextAction = recommendNextAction({
    pendingApprovals: humanApprovals.filter((a) => a.state === 'pending').length,
    blockedTasks: counts.blocked,
    errorAgents: agents.filter((a) => a.state === 'error' || a.state === 'outOfQuota').length,
    criticalBudgets: budgets.filter((b) => budgetSeverity(b) !== 'ok').length,
  });
  const progress = aggregateProgress(tasks);
  const acceptedProgress = canonicalProgress?.percentage ?? progress.approved;
  const displayedProgress = technical
    ? progress
    : { ...progress, approved: acceptedProgress };

  return (
    <div className="flex flex-col gap-6">
      <section className="poseidon-hero flex flex-wrap items-center gap-4 rounded-xl border border-border p-5 shadow-glow sm:p-6">
        <span
          aria-hidden="true"
          className="inline-flex size-12 shrink-0 items-center justify-center rounded-xl bg-gradient-brand text-primary-foreground shadow-glow"
        >
          <Activity className="size-6" />
        </span>
        <div className="flex min-w-0 flex-col gap-1">
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="font-heading text-3xl font-bold tracking-tightest">
              {t('features.cockpit.title')}
            </h1>
            {/* O cockpit mostra cotas/orçamento e saúde: quando a origem é fixture,
                dizemos isso explicitamente em vez de passar por dado real (§15). */}
            <SimulatedModeBadge />
            <Badge variant="outline">{t(`settings.presentation.modes.${presentation.mode}`)}</Badge>
          </div>
          <p className="text-sm text-foreground-muted">{t('features.cockpit.description')}</p>
        </div>
      </section>

      {loading ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-24 w-full" />
          <div className="grid gap-3 lg:grid-cols-2">
            <Skeleton className="h-40 w-full" />
            <Skeleton className="h-40 w-full" />
          </div>
        </div>
      ) : errored ? (
        <div className="flex flex-col items-start gap-3">
          <p role="alert" className="text-sm text-error">
            {t('common.states.errorBody')}
          </p>
          <Button type="button" variant="outline" onClick={retryAll}>
            {t('common.actions.retry')}
          </Button>
        </div>
      ) : !activeProject ? (
        // Sem projeto ativo, o checklist do golden path é a orientação
        // primária e dono da CTA única (§4): aqui mantemos apenas o contexto,
        // sem repetir um botão semanticamente equivalente.
        <>
          <GoldenPathChecklist hideWhenComplete />
          <Card>
            <CardHeader>
              <CardTitle>{t('cockpit.empty.title')}</CardTitle>
            </CardHeader>
            <CardContent>
              <p className="text-sm text-foreground-muted">{t('cockpit.empty.body')}</p>
            </CardContent>
          </Card>
        </>
      ) : (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <FleetOverview agents={agents} taskCounts={counts} mode={presentationMode} />
          <div className="lg:col-span-2">
            <GoldenPathChecklist hideWhenComplete />
          </div>
          {technical ? (
            <>
              {featureFlags.governanceContractUi && (
                <GovernanceHealthCard projectId={activeProject.id} />
              )}
              <WorkflowProgress
                phases={workflowData.phases}
                currentPhase={phase}
                progress={canonicalProgress}
                progressByPhaseId={phaseProgress.byPhaseId}
                gates={workflowData.gates}
                humanGateCount={humanGateCount}
                isPending={phaseProgress.isPending}
                isError={phaseProgress.isError}
              />
            </>
          ) : (
            <>
              <ProjectTimeline
                phases={workflowData.phases}
                gates={workflowData.gates}
                overallProgress={acceptedProgress}
              />
              <TeamCapacityCard />
              <PendingApprovalsCard approvals={humanApprovals} />
            </>
          )}
          <Card className="lg:col-span-2">
            <CardHeader>
              <CardTitle>{t('cockpit.progress.globalTitle')}</CardTitle>
              <p className="text-xs text-foreground-muted">
                {t(
                  technical
                    ? 'cockpit.progress.technicalSubtitle'
                    : 'cockpit.progress.businessSubtitle',
                )}
              </p>
            </CardHeader>
            <CardContent>
              <ProgressTracks
                progress={displayedProgress}
                evidence={progressEvidence(tasks)}
                mode={presentationMode}
                currentPhaseName={phase?.name}
              />
            </CardContent>
          </Card>
          <NextActionCard actionKey={nextAction} />
          <TasksByStateChart counts={counts} />
          <BlockedTasksCard tasks={tasks} mode={presentationMode} />
          {technical && <PendingApprovalsCard approvals={humanApprovals} mode="technical" />}
          {technical && <QuotaCard budgets={budgets} />}
          <div className="lg:col-span-2">
            <ActivityFeed events={events} mode={presentationMode} />
          </div>
        </div>
      )}
    </div>
  );
}
