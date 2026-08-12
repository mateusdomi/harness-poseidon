import type {
  Agent,
  Phase,
  Project,
  Task,
  V3ProjectContext,
  Workflow,
  WorkflowRun,
} from '@/api';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { projectV3Lifecycle, type V3LifecycleMacro } from './v3-lifecycle';

export interface ProjectOperationalSummary {
  lifecycleState: V3LifecycleMacro;
  lifecycleLabel: string;
  lifecycleStatus: string;
  responsibleName: string | null;
  health: 'healthy' | 'attention' | 'unavailable';
  runningExecutions: number;
}

export interface ProjectOperationalSource {
  tasks: Task[];
  agents: Agent[];
  workflows: Workflow[];
  runs: WorkflowRun[];
  phases: Phase[];
}

/** Projeção somente de dados reais já publicados pelos contratos operacionais. */
export function deriveProjectOperationalSummary(
  project: Project,
  source: ProjectOperationalSource,
  v3Context?: Pick<V3ProjectContext, 'currentLifecycleState' | 'openQuestions'> | null,
): ProjectOperationalSummary {
  const tasks = source.tasks.filter((task) => task.projectId === project.id);
  const workflow = source.workflows.find((item) => item.projectId === project.id) ?? null;
  const run = workflow
    ? source.runs
        .filter((item) => item.workflowId === workflow.id)
        .sort((a, b) => b.startedAt.localeCompare(a.startedAt))[0] ?? null
    : null;
  const phase = run
    ? source.phases.find((item) => item.runId === run.id && item.state === 'active') ?? null
    : null;
  const responsible =
    source.agents.find((agent) => agent.id === project.chiefAgentId) ?? null;
  const hasAttention =
    (v3Context?.openQuestions.length ?? 0) > 0 ||
    tasks.some((task) => task.state === 'blocked') ||
    source.agents.some(
      (agent) =>
        agent.projectId === project.id &&
        (agent.state === 'error' || agent.state === 'outOfQuota'),
    );
  const v3Lifecycle = v3Context?.currentLifecycleState
    ? projectV3Lifecycle(v3Context.currentLifecycleState)
    : null;

  return {
    lifecycleState: v3Lifecycle?.macro ?? (run ? 'build' : 'understand'),
    lifecycleLabel: v3Lifecycle?.macroLabel ?? (run ? 'Desenvolvimento' : 'Entendimento'),
    lifecycleStatus: v3Lifecycle?.statusLabel ?? (run ? 'Em andamento' : 'Em entendimento'),
    responsibleName:
      responsible === null
        ? null
        : resolveAgentIdentity('chief-orchestrator', responsible.name).humanName,
    health: tasks.length === 0 && !workflow ? 'unavailable' : hasAttention ? 'attention' : 'healthy',
    runningExecutions: run && phase ? 1 : 0,
  };
}
