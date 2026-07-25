import { useTranslation } from 'react-i18next';

import type { Agent, AgentDefinition, Attempt, AuditEvent, Model, Skill, Task, Tool } from '@/api';
import { Badge } from '@/design-system';
import {
  agentHistory,
  agentModelRoute,
  agentSkills,
  agentTools,
  compatibleModels,
} from '@/features/agents/lib/agents-derive';
import { ManagedAgentAvatar } from '@/features/shared/components/managed-agent-avatar';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import {
  formatCurrencyUSD,
  formatDurationMs,
  formatNumber,
  formatRelativeTime,
} from '@/lib/format';
import { agentStateVariant, attemptStateVariant, componentStateVariant } from '@/lib/status';

export interface AgentDetailProps {
  agent: Agent;
  definition: AgentDefinition | null;
  skills: Skill[];
  tools: Tool[];
  models: Model[];
  tasks: Task[];
  attempts: Attempt[];
  auditEvents: AuditEvent[];
  now: Date;
  onClose: () => void;
}

function SectionTitle({ title }: { title: string }) {
  return <h3 className="text-sm font-semibold">{title}</h3>;
}

/**
 * Detalhe do agente (modal): definição/persona (descrição atual — o
 * contrato NÃO versiona persona/instruções), skills, ferramentas
 * permitidas, modelos compatíveis, modelo/rota em uso (override humano
 * vs. padrão da definição), esforço tipado/mapeado pelo provider, impacto
 * estimado pelas tarifas do catálogo e histórico (auditoria + attempts).
 */
export function AgentDetail({
  agent,
  definition,
  skills,
  tools,
  models,
  tasks,
  attempts,
  auditEvents,
  now,
  onClose,
}: AgentDetailProps) {
  const { t } = useTranslation();

  const resolvedSkills = agentSkills(definition, skills);
  const resolvedTools = agentTools(definition, tools);
  const enabledModels = compatibleModels(models);
  const modelRoute = agentModelRoute(agent, definition, models);
  const effort = agent.effort ?? definition?.defaultEffort ?? null;
  const effortMapping = modelRoute.current?.effortMappings.find(
    (mapping) => mapping.effort === effort,
  );
  const history = agentHistory(agent.id, auditEvents, attempts);
  const identity = resolveAgentIdentity(definition?.key, agent.name);

  function taskTitle(taskId: string): string {
    return tasks.find((task) => task.id === taskId)?.title ?? '';
  }

  return (
    <ModalDialog label={agent.name} onClose={onClose} className="max-w-2xl">
      <div className="flex items-start gap-3 pr-10">
        <ManagedAgentAvatar
          alias={definition?.key ?? agent.name}
          fallbackName={agent.name}
          roleLabel={identity.roleLabel}
          size={64}
        />
        <div className="flex min-w-0 flex-col gap-1">
          <h2 className="font-heading text-xl font-semibold">{identity.humanName}</h2>
          {/* Alias técnico da instância (transparência). */}
          {identity.humanName !== agent.name && (
            <p className="text-xs text-foreground-muted">{agent.name}</p>
          )}
          <p className="text-sm text-foreground-muted">
            {definition?.name}
            {definition?.specialty ? ` · ${definition.specialty}` : ''}
          </p>
          <div className="mt-1 flex flex-wrap items-center gap-2">
            <Badge variant={agentStateVariant(agent.state)}>
              {t(`status.agentState.${agent.state}`)}
            </Badge>
            <span className="text-xs text-foreground-muted">
              {agent.lastHeartbeatAt
                ? t('agents.detail.lastHeartbeat', {
                    time: formatRelativeTime(agent.lastHeartbeatAt, 'pt-BR', now),
                  })
                : t('agents.detail.heartbeatNever')}
            </span>
          </div>
        </div>
      </div>

      {definition && (
        <section className="flex flex-col gap-1">
          <SectionTitle title={t('agents.detail.description')} />
          <p className="text-sm text-foreground-muted">{definition.description}</p>
        </section>
      )}

      <section className="flex flex-col gap-2">
        <SectionTitle title={t('agents.detail.skills')} />
        {resolvedSkills.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('agents.detail.skillsEmpty')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {resolvedSkills.map((skill) => (
              <li key={skill.id} className="flex flex-wrap items-center gap-2">
                <span className="text-sm font-medium">{skill.name}</span>
                <Badge variant="outline">
                  {t('agents.detail.version', { version: skill.version })}
                </Badge>
                <Badge variant={componentStateVariant(skill.state)}>
                  {t(`status.componentState.${skill.state}`)}
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <SectionTitle title={t('agents.detail.tools')} />
        {resolvedTools.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('agents.detail.toolsEmpty')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {resolvedTools.map((tool) => (
              <li key={tool.id} className="flex flex-wrap items-center gap-2">
                <span className="text-sm font-medium">{tool.name}</span>
                <Badge variant="outline">{t(`status.toolKind.${tool.kind}`)}</Badge>
                <Badge variant={componentStateVariant(tool.state)}>
                  {t(`status.componentState.${tool.state}`)}
                </Badge>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <SectionTitle title={t('agents.detail.models')} />
        {enabledModels.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('agents.detail.modelsEmpty')}</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {enabledModels.map((model) => (
              <li key={model.id} className="flex flex-wrap items-center gap-2">
                <span className="text-sm font-medium">{model.displayName}</span>
                <span className="text-xs text-foreground-muted">
                  {t('agents.detail.modelContext', {
                    total: formatNumber(model.contextWindow),
                  })}
                </span>
                {model.id === definition?.defaultModelId && (
                  <Badge variant="brand">{t('agents.detail.modelDefault')}</Badge>
                )}
                {model.id === agent.modelId && (
                  <Badge variant="warning">{t('agents.detail.modelOverride')}</Badge>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="flex flex-col gap-2">
        <SectionTitle title={t('agents.detail.route.title')} />
        <dl className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center gap-2">
            <dt className="text-xs font-medium text-foreground-muted">
              {t('agents.detail.route.current')}
            </dt>
            <dd className="flex flex-wrap items-center gap-2">
              <span className="text-sm font-medium">
                {modelRoute.current?.displayName ?? t('agents.detail.route.unresolved')}
              </span>
              {modelRoute.source === 'override' ? (
                <Badge variant="warning">{t('agents.detail.route.sourceOverride')}</Badge>
              ) : (
                <Badge variant="outline">{t('agents.detail.route.sourceDefault')}</Badge>
              )}
            </dd>
          </div>
          <div className="flex flex-wrap items-start gap-2">
            <dt className="text-xs font-medium text-foreground-muted">
              {t('agents.detail.route.reason')}
            </dt>
            <dd className="text-sm text-foreground-muted">
              {agent.selectionReason ??
                t(
                  modelRoute.source === 'override'
                    ? 'agents.detail.route.reasonOverride'
                    : 'agents.detail.route.reasonDefault',
                )}
            </dd>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <dt className="text-xs font-medium text-foreground-muted">
              {t('agents.detail.route.effort')}
            </dt>
            <dd className="flex flex-wrap items-center gap-2 text-sm">
              {effort ? (
                <>
                  <Badge variant="outline">{t(`agents.detail.route.effortLevel.${effort}`)}</Badge>
                  <span className="text-foreground-muted">
                    {agent.providerEffortValue
                      ? t('agents.detail.route.providerEffort', {
                          value: agent.providerEffortValue,
                        })
                      : effortMapping
                        ? t('agents.detail.route.providerEffort', {
                            value: effortMapping.providerValue,
                          })
                        : t('agents.detail.route.effortUnavailable')}
                  </span>
                </>
              ) : (
                <span className="text-foreground-muted">
                  {t('agents.detail.route.effortAutomatic')}
                </span>
              )}
            </dd>
          </div>
          <div className="flex flex-wrap items-start gap-2">
            <dt className="text-xs font-medium text-foreground-muted">
              {t('agents.detail.route.estimatedImpact')}
            </dt>
            <dd className="text-sm text-foreground-muted">
              {modelRoute.current?.costPer1kInputUsd === null ||
              modelRoute.current?.costPer1kInputUsd === undefined ||
              modelRoute.current.costPer1kOutputUsd === null
                ? t('agents.detail.route.estimatedImpactLocal')
                : t('agents.detail.route.estimatedImpactCost', {
                    input: formatCurrencyUSD(modelRoute.current.costPer1kInputUsd),
                    output: formatCurrencyUSD(modelRoute.current.costPer1kOutputUsd),
                  })}
            </dd>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <dt className="text-xs font-medium text-foreground-muted">
              {t('agents.detail.route.defaultModel')}
            </dt>
            <dd className="text-sm">
              {modelRoute.defaultModel?.displayName ?? t('agents.detail.route.unresolved')}
            </dd>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <dt className="text-xs font-medium text-foreground-muted">
              {t('agents.detail.route.fallbacks')}
            </dt>
            <dd className="flex flex-wrap items-center gap-1">
              {modelRoute.fallbacks.length === 0 ? (
                <span className="text-sm text-foreground-muted">
                  {t('agents.detail.route.fallbacksEmpty')}
                </span>
              ) : (
                modelRoute.fallbacks.map((model) => (
                  <Badge key={model.id} variant="outline">
                    {model.displayName}
                  </Badge>
                ))
              )}
            </dd>
          </div>
        </dl>
      </section>

      <section className="flex flex-col gap-2">
        <SectionTitle title={t('agents.detail.history')} />
        {history.length === 0 ? (
          <p className="text-sm text-foreground-muted">{t('agents.detail.historyEmpty')}</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {history.map((entry) =>
              entry.kind === 'audit' ? (
                <li key={entry.event.id} className="flex flex-col gap-0.5">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-medium">{entry.event.action}</span>
                    <span className="text-xs text-foreground-muted">
                      {formatRelativeTime(entry.event.occurredAt, 'pt-BR', now)}
                    </span>
                  </div>
                  {entry.event.detail && (
                    <p className="text-xs text-foreground-muted">{entry.event.detail}</p>
                  )}
                </li>
              ) : (
                <li key={entry.attempt.id} className="flex flex-col gap-0.5">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="text-sm font-medium">
                      {t('agents.detail.attempt', { number: entry.attempt.number })}
                      {taskTitle(entry.attempt.taskId)
                        ? ` — ${taskTitle(entry.attempt.taskId)}`
                        : ''}
                    </span>
                    <Badge variant={attemptStateVariant(entry.attempt.state)}>
                      {t(`status.attemptState.${entry.attempt.state}`)}
                    </Badge>
                    <span className="text-xs text-foreground-muted">
                      {formatRelativeTime(entry.attempt.startedAt, 'pt-BR', now)}
                    </span>
                  </div>
                  <p className="text-xs text-foreground-muted">
                    {formatCurrencyUSD(entry.attempt.costUsd)}
                    {entry.attempt.durationMs !== null
                      ? ` · ${formatDurationMs(entry.attempt.durationMs)}`
                      : ''}
                  </p>
                </li>
              ),
            )}
          </ul>
        )}
      </section>
    </ModalDialog>
  );
}
