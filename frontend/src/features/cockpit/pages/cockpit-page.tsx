import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';

import { Button, Card, CardContent, CardHeader, CardTitle, Select, Skeleton } from '@/design-system';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { ActivityFeed } from '@/features/cockpit/components/activity-feed';
import { BlockedTasksCard, PendingApprovalsCard } from '@/features/cockpit/components/attention-cards';
import { AgentsHealthCard, QuotaCard } from '@/features/cockpit/components/health-cards';
import { NextActionCard } from '@/features/cockpit/components/next-action-card';
import { GovernanceHealthCard } from '@/features/cockpit/components/governance-health-card';
import { PhaseSummary } from '@/features/cockpit/components/phase-summary';
import { ProgressTracks } from '@/features/cockpit/components/progress-tracks';
import { TaskStateCounters } from '@/features/cockpit/components/task-state-counters';
import {
  useCockpitActivity,
  useCockpitAgents,
  useCockpitApprovals,
  useCockpitBudgets,
  useCockpitRealtime,
  useCockpitTasks,
  useCockpitWorkflow,
} from '@/features/cockpit/hooks/use-cockpit';
import {
  aggregateProgress,
  budgetSeverity,
  countTasksByState,
  currentPhase,
  recommendNextAction,
} from '@/features/cockpit/lib/cockpit-derive';
import { featureFlags } from '@/config/features';

export default function CockpitPage() {
  const { t } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();

  const projectId = activeProject?.id ?? null;
  useCockpitRealtime(projectId);

  const tasksQuery = useCockpitTasks(projectId);
  const approvalsQuery = useCockpitApprovals(projectId);
  const agentsQuery = useCockpitAgents(projectId);
  const budgetsQuery = useCockpitBudgets();
  const activityQuery = useCockpitActivity();
  const workflowData = useCockpitWorkflow(projectId);

  const loading =
    isPending ||
    tasksQuery.isLoading ||
    approvalsQuery.isLoading ||
    agentsQuery.isLoading ||
    budgetsQuery.isLoading ||
    activityQuery.isLoading ||
    workflowData.isPending;
  const errored =
    isError ||
    tasksQuery.isError ||
    approvalsQuery.isError ||
    agentsQuery.isError ||
    budgetsQuery.isError ||
    activityQuery.isError ||
    workflowData.isError;

  function retryAll() {
    refetch();
    void tasksQuery.refetch();
    void approvalsQuery.refetch();
    void agentsQuery.refetch();
    void budgetsQuery.refetch();
    void activityQuery.refetch();
    workflowData.refetch();
  }

  const tasks = tasksQuery.data ?? [];
  const approvals = approvalsQuery.data ?? [];
  const agents = agentsQuery.data ?? [];
  const budgets = budgetsQuery.data ?? [];
  const events = activityQuery.data ?? [];
  const phase = currentPhase(workflowData.phases);
  const counts = countTasksByState(tasks);
  const nextAction = recommendNextAction({
    pendingApprovals: approvals.filter((a) => a.state === 'pending').length,
    blockedTasks: counts.blocked,
    errorAgents: agents.filter((a) => a.state === 'error' || a.state === 'outOfQuota').length,
    criticalBudgets: budgets.filter((b) => budgetSeverity(b) !== 'ok').length,
  });

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.cockpit.title')}</h1>
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="cockpit-project" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="cockpit-project"
              className="w-auto min-w-48"
              value={activeProject?.id ?? ''}
              onChange={(event) => setActiveProject(event.target.value)}
            >
              {projects.map((project) => (
                <option key={project.id} value={project.id}>
                  {project.name}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>

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
        <Card>
          <CardHeader>
            <CardTitle>{t('cockpit.empty.title')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col items-start gap-3">
            <p className="text-sm text-foreground-muted">{t('cockpit.empty.body')}</p>
            <Button asChild>
              <Link to="/projects">{t('cockpit.empty.cta')}</Link>
            </Button>
          </CardContent>
        </Card>
      ) : (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          {featureFlags.governanceContractUi && <GovernanceHealthCard projectId={activeProject.id} />}
          <PhaseSummary phase={phase} gates={workflowData.gates} tasks={tasks} />
          <NextActionCard actionKey={nextAction} />
          <Card className="lg:col-span-2">
            <CardHeader>
              <CardTitle>{t('cockpit.progress.globalTitle')}</CardTitle>
            </CardHeader>
            <CardContent>
              <ProgressTracks progress={aggregateProgress(tasks)} />
            </CardContent>
          </Card>
          <div className="lg:col-span-2">
            <TaskStateCounters counts={counts} />
          </div>
          <BlockedTasksCard tasks={tasks} />
          <PendingApprovalsCard approvals={approvals} />
          <AgentsHealthCard agents={agents} />
          <QuotaCard budgets={budgets} />
          <div className="lg:col-span-2">
            <ActivityFeed events={events} />
          </div>
        </div>
      )}
    </div>
  );
}
