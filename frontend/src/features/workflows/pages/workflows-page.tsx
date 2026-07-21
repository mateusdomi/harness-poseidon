import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Info } from 'lucide-react';

import { Badge, Button, Card, CardContent, Select, Skeleton } from '@/design-system';
import { workflowRunStateVariant } from '@/lib/status';
import { OperationModeCard } from '@/features/workflows/components/operation-mode-card';
import { PhaseStepper } from '@/features/workflows/components/phase-stepper';
import { TemplateAdmin } from '@/features/workflows/components/template-admin';
import { WorkflowOnboardingEmpty } from '@/features/workflows/components/workflow-onboarding-empty';
import {
  useActiveRun,
  useAgentDefinitions,
  useLinkWorkflowTemplate,
  useProjectWorkflow,
  useRunDetails,
  useWorkflowDocuments,
  useWorkflowsRealtime,
  useWorkflowTemplates,
  useWorkflowUsage,
} from '@/features/workflows/hooks/use-workflows';
import { useToolsCatalog } from '@/features/tools/hooks/use-tools';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';

/**
 * Workflows do projeto ativo: stepper das fases do run (com gates e
 * documentos por fase), modo de operação com troca protegida por aceite
 * de risco e administração de templates/versões.
 */
export default function UworkflowsPage() {
  const { t } = useTranslation();
  const { projects, activeProject, setActiveProject, isPending, isError, refetch } =
    useActiveProject();
  const projectId = activeProject?.id ?? null;

  const workflowQuery = useProjectWorkflow(projectId);
  const workflow = workflowQuery.data ?? null;
  const runQuery = useActiveRun(workflow?.id ?? null);
  const run = runQuery.data ?? null;
  const { phases, gates, isPending: runPending, isError: runError, refetch: refetchRun } =
    useRunDetails(run?.id ?? null);
  const documentsQuery = useWorkflowDocuments(projectId);
  const {
    templates,
    versions,
    isPending: templatesPending,
    isError: templatesError,
    refetch: refetchTemplates,
  } = useWorkflowTemplates();
  const agentDefinitionsQuery = useAgentDefinitions();
  const usage = useWorkflowUsage();
  const toolsCatalog = useToolsCatalog();
  const linkTemplate = useLinkWorkflowTemplate();
  useWorkflowsRealtime(projectId);

  const loading =
    isPending ||
    workflowQuery.isLoading ||
    (workflow !== null && (runQuery.isLoading || runPending)) ||
    templatesPending;
  const errored =
    isError || workflowQuery.isError || runQuery.isError || runError || templatesError;

  function retryAll() {
    refetch();
    void workflowQuery.refetch();
    void runQuery.refetch();
    refetchRun();
    refetchTemplates();
  }

  const activeVersion = versions.find((v) => v.id === workflow?.activeVersionId) ?? null;
  const runVersion = versions.find((v) => v.id === run?.versionId) ?? null;
  const gateNames = activeVersion ? Object.values(activeVersion.gatesByPhase).flat() : [];

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="font-heading text-2xl font-semibold">{t('features.workflows.title')}</h1>
        {projects.length > 0 && (
          <div className="ml-auto flex items-center gap-2">
            <label htmlFor="workflows-project" className="text-sm text-foreground-muted">
              {t('cockpit.projectSelector.label')}
            </label>
            <Select
              id="workflows-project"
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
        <div className="flex flex-col gap-4" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-32 w-full" />
          <Skeleton className="h-64 w-full" />
          <Skeleton className="h-40 w-full" />
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
          <CardContent className="flex flex-col items-start gap-3 p-6">
            <p className="text-sm text-foreground-muted">{t('workflows.noProject.body')}</p>
            <Button asChild>
              <Link to="/projects">{t('workflows.noProject.cta')}</Link>
            </Button>
          </CardContent>
        </Card>
      ) : !workflow ? (
        <WorkflowOnboardingEmpty
          templates={templates}
          versions={versions}
          linking={linkTemplate.isPending}
          error={linkTemplate.isError}
          onLink={(templateId) =>
            linkTemplate.mutate({ projectId: activeProject.id, templateId })
          }
        />
      ) : (
        <>
          {run && runVersion && (
            <p className="flex items-start gap-2 rounded-lg border border-border bg-surface p-3 text-xs text-foreground-muted">
              <Info aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
              <span>
                {t('workflows.run.versionPinned', {
                  version: runVersion.version,
                  template: templates.find((tpl) => tpl.id === workflow.templateId)?.name ?? '',
                })}{' '}
                <Badge variant={workflowRunStateVariant(run.state)}>
                  {t(`status.workflowRunState.${run.state}`)}
                </Badge>
              </span>
            </p>
          )}

          <OperationModeCard workflow={workflow} gateNames={gateNames} />

          {run && phases.length > 0 && (
            <section aria-labelledby="phases-title" className="flex flex-col gap-3">
              <h2 id="phases-title" className="font-heading text-lg font-semibold">
                {t('workflows.phases.title')}
              </h2>
              <PhaseStepper
                phases={phases}
                gates={gates}
                documents={documentsQuery.data ?? []}
              />
            </section>
          )}

          <TemplateAdmin
            templates={templates}
            versions={versions}
            agentDefinitions={agentDefinitionsQuery.data ?? []}
            skills={toolsCatalog.skills}
            tools={toolsCatalog.tools}
            usedTemplateIds={usage.usedTemplateIds}
            usedVersionIds={usage.usedVersionIds}
            activeProjectId={projectId}
            activeProjectWorkflow={workflow}
            activeRunVersion={runVersion}
          />
        </>
      )}
    </div>
  );
}
