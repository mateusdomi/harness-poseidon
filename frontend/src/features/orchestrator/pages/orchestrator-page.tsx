import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router-dom';

import { Button, Card, CardContent, CardHeader, CardTitle, Skeleton } from '@/design-system';
import { AgentGrid } from '@/features/orchestrator/components/agent-grid';
import { DefinitionsTab } from '@/features/orchestrator/components/definitions-tab';
import { ChiefCard } from '@/features/orchestrator/components/chief-card';
import {
  useChiefTurnState,
  useOrchestratorData,
  useOrchestratorRealtime,
} from '@/features/orchestrator/hooks/use-orchestrator';
import {
  chiefBudgets,
  resolveChiefAccount,
  resolveChiefModel,
  resolveModelBindingSource,
} from '@/features/orchestrator/lib/orchestrator-derive';
import { useActiveProject } from '@/features/shared/hooks/use-active-project';
import { useNow } from '@/features/shared/hooks/use-now';
import { useProjectWorkflow } from '@/features/workflows/hooks/use-workflows';

/**
 * Tela do orquestrador (/orchestrator): card do chefe do projeto ativo
 * (estado, turno, saúde, modelo/conta/cota, ações) + grade de agentes
 * especialistas agrupada por estado, tudo vivo via realtime.
 */
export default function UorchestratorPage() {
  const { t } = useTranslation();
  // Abas via query param: `?tab=overview` (padrão) | `?tab=definitions`
  // (com deep-link `&definition=<id>`, tratado pela própria aba).
  const [searchParams, setSearchParams] = useSearchParams();
  const activeTab = searchParams.get('tab') === 'definitions' ? 'definitions' : 'overview';
  const {
    activeProject,
    isPending: projectPending,
    isError: projectError,
    refetch: refetchProjects,
  } = useActiveProject();

  const projectId = activeProject?.id ?? null;
  const data = useOrchestratorData(projectId);
  // Relógio de 1s: duração das tentativas em execução atualiza "ao vivo".
  const now = useNow(1_000);

  const projectAgents = data.agents;
  const projectAgentIds = new Set(projectAgents.map((agent) => agent.id));
  const projectAttempts = data.attempts.filter((attempt) => projectAgentIds.has(attempt.agentId));
  const runningAttemptIds = projectAttempts
    .filter((attempt) => attempt.state === 'running')
    .map((attempt) => attempt.id);

  useOrchestratorRealtime(projectId, runningAttemptIds);
  const turnState = useChiefTurnState(data.conversations.map((conversation) => conversation.id));

  const loading = projectPending || data.isPending;
  const errored = projectError || data.isError;

  function retryAll() {
    refetchProjects();
    data.refetch();
  }

  const chief = activeProject
    ? (projectAgents.find((agent) => agent.id === activeProject.chiefAgentId) ?? null)
    : null;
  const definition = chief
    ? (data.definitions.find((entry) => entry.id === chief.definitionId) ?? null)
    : null;
  const model = chief ? resolveChiefModel(chief, definition, data.models) : null;
  const account = resolveChiefAccount(model, data.accounts);
  // Prontidão real (§15): workflow vinculado + execução corrente + procedência
  // do vínculo do modelo. Nenhum destes é presumido.
  const workflowQuery = useProjectWorkflow(projectId);
  const modelBinding = chief ? resolveModelBindingSource(chief, definition) : 'none';
  const chiefIsRunning = chief
    ? projectAttempts.some(
        (attempt) => attempt.agentId === chief.id && attempt.state === 'running',
      )
    : false;
  const budgets = activeProject
    ? chiefBudgets(data.budgets, activeProject.id, account?.id ?? null)
    : [];
  // A grade mostra os especialistas orquestrados — o chefe tem card próprio.
  const gridAgents = projectAgents.filter((agent) => {
    const agentDefinition = data.definitions.find((entry) => entry.id === agent.definitionId);
    return agentDefinition ? agentDefinition.role !== 'chief' : agent.id !== activeProject?.chiefAgentId;
  });

  function selectTab(tab: 'overview' | 'definitions') {
    setSearchParams(tab === 'definitions' ? { tab: 'definitions' } : {});
  }

  return (
    <div className="flex flex-col gap-6">
      <h1 className="font-heading text-2xl font-semibold">{t('features.orchestrator.title')}</h1>

      <div
        role="tablist"
        aria-label={t('orchestrator.tabs.label')}
        className="flex flex-wrap gap-1"
      >
        {(['overview', 'definitions'] as const).map((tab) => (
          <button
            key={tab}
            type="button"
            role="tab"
            id={`orchestrator-tab-${tab}`}
            aria-selected={activeTab === tab}
            onClick={() => selectTab(tab)}
            className={
              activeTab === tab
                ? 'min-h-touch rounded-md border border-brand px-3 py-2 text-sm font-medium text-brand-strong'
                : 'min-h-touch rounded-md border border-border px-3 py-2 text-sm text-foreground-muted hover:text-foreground'
            }
          >
            {t(`orchestrator.tabs.${tab}`)}
          </button>
        ))}
      </div>

      {activeTab === 'definitions' ? (
        <DefinitionsTab />
      ) : loading ? (
        <div className="flex flex-col gap-3" role="status" aria-label={t('common.states.loading')}>
          <Skeleton className="h-48 w-full" />
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
            <Skeleton className="h-36 w-full" />
            <Skeleton className="h-36 w-full" />
            <Skeleton className="h-36 w-full" />
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
      ) : !activeProject || !chief ? (
        <Card>
          <CardHeader>
            <CardTitle>{t('orchestrator.empty.title')}</CardTitle>
          </CardHeader>
          <CardContent className="flex flex-col items-start gap-3">
            <p className="text-sm text-foreground-muted">{t('orchestrator.empty.body')}</p>
            <Button asChild>
              <Link to="/projects">{t('orchestrator.empty.cta')}</Link>
            </Button>
          </CardContent>
        </Card>
      ) : (
        <>
          <ChiefCard
            project={activeProject}
            chief={chief}
            model={model}
            account={account}
            budgets={budgets}
            turnState={turnState}
            now={now}
            hasWorkflow={workflowQuery.data != null}
            isRunning={chiefIsRunning}
            modelBinding={modelBinding}
          />
          <AgentGrid agents={gridAgents} tasks={data.tasks} attempts={projectAttempts} now={now} />
        </>
      )}
    </div>
  );
}
