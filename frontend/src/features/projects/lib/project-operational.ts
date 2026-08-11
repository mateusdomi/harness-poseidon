import type {
  Agent,
  Phase,
  Project,
  Task,
  Workflow,
  WorkflowRun,
} from '@/api';
import { resolveAgentIdentity } from '@/lib/agent-persona';

export interface ProjectOperationalSummary {
  lifecycleState: 'understand' | 'build' | 'validate' | 'acceptance';
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
    tasks.some((task) => task.state === 'blocked') ||
    source.agents.some(
      (agent) =>
        agent.projectId === project.id &&
        (agent.state === 'error' || agent.state === 'outOfQuota'),
    );

  return {
    lifecycleState: run ? 'build' : 'understand',
    responsibleName:
      responsible === null
        ? null
        : resolveAgentIdentity('chief-orchestrator', responsible.name).humanName,
    health: tasks.length === 0 && !workflow ? 'unavailable' : hasAttention ? 'attention' : 'healthy',
    runningExecutions: run && phase ? 1 : 0,
  };
}
