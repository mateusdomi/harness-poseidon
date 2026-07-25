import type {
  Agent,
  Phase,
  Project,
  Task,
  Workflow,
  WorkflowRun,
  WorkflowTemplate,
} from '@/api';
import { resolveAgentIdentity } from '@/lib/agent-persona';

export interface ProjectOperationalSummary {
  phaseName: string | null;
  completedTasks: number;
  totalTasks: number;
  progressPercent: number | null;
  responsibleName: string | null;
  health: 'healthy' | 'attention' | 'unavailable';
  workflowName: string | null;
}

export interface ProjectOperationalSource {
  tasks: Task[];
  agents: Agent[];
  workflows: Workflow[];
  runs: WorkflowRun[];
  phases: Phase[];
  templates: WorkflowTemplate[];
}

/** Projeção somente de dados reais já publicados pelos contratos operacionais. */
export function deriveProjectOperationalSummary(
  project: Project,
  source: ProjectOperationalSource,
): ProjectOperationalSummary {
  const tasks = source.tasks.filter((task) => task.projectId === project.id);
  const completedTasks = tasks.filter((task) => task.state === 'done').length;
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
    phaseName: phase?.name ?? null,
    completedTasks,
    totalTasks: tasks.length,
    progressPercent:
      tasks.length === 0 ? null : Math.round((completedTasks / tasks.length) * 100),
    responsibleName:
      responsible === null
        ? null
        : resolveAgentIdentity('chief-orchestrator', responsible.name).humanName,
    health: tasks.length === 0 && !workflow ? 'unavailable' : hasAttention ? 'attention' : 'healthy',
    workflowName:
      workflow === null
        ? null
        : (source.templates.find((template) => template.id === workflow.templateId)?.name ?? null),
  };
}
