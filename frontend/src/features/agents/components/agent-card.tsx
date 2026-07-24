import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import { Info, Settings } from 'lucide-react';

import type { Agent, AgentDefinition, Skill } from '@/api';
import { Badge, Button, Card, CardContent } from '@/design-system';
import { AgentAvatar } from '@/features/shared/components/agent-avatar';
import type { DerivedAgentMetrics } from '@/features/agents/lib/agents-derive';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { agentStateVariant } from '@/lib/status';

export interface AgentCardProps {
  agent: Agent;
  definition: AgentDefinition | null;
  /** Skills da definição já resolvidas (ordem declarada). */
  skills: Skill[];
  metrics: DerivedAgentMetrics;
  onSelect: (agent: Agent) => void;
}

/** Rótulo da métrica + ajuda: a definição exata fica visível no tooltip. */
function MetricItem({ label, help, value }: { label: string; help: string; value: number }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="flex items-center gap-1 text-xs text-foreground-muted">
        {label}
        <span
          role="img"
          tabIndex={0}
          title={help}
          aria-label={help}
          className="inline-flex size-4 cursor-help items-center justify-center rounded-full text-foreground-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent"
        >
          <Info aria-hidden="true" className="size-3.5" />
        </span>
      </span>
      <span className="text-sm font-semibold tabular-nums">{value}</span>
    </div>
  );
}

/**
 * Card de um agente no organograma: identidade (nome, definição e
 * especialidade), estado, capacidades (skills) e as três métricas
 * objetivas com definição visível de cada uma.
 */
export function AgentCard({ agent, definition, skills, metrics, onSelect }: AgentCardProps) {
  const { t } = useTranslation();
  // Humaniza a persona pelo alias da definição; cai para o nome da instância
  // quando a definição não está no mapa. Aliases técnicos ficam como subtítulo.
  const identity = resolveAgentIdentity(definition?.key, agent.name);

  return (
    <Card className="flex h-full flex-col">
      <CardContent className="flex flex-1 flex-col gap-3 p-4">
        <div className="flex items-start justify-between gap-2">
          <div className="flex min-w-0 items-start gap-2.5">
            <AgentAvatar name={identity.humanName} size={40} />
            <div className="flex min-w-0 flex-col">
              <span className="truncate font-heading text-base font-semibold">
                {identity.humanName}
              </span>
              {/* Alias técnico da instância (ex.: "Chefe — Poseidon Frontend"):
                  transparência sem esconder a identidade real do sistema. */}
              {identity.humanName !== agent.name && (
                <span className="truncate text-xs text-foreground-muted">{agent.name}</span>
              )}
              {definition && (
                <span className="truncate text-xs text-foreground-muted">
                  {definition.name}
                  {definition.specialty ? ` · ${definition.specialty}` : ''}
                </span>
              )}
            </div>
          </div>
          <Badge variant={agentStateVariant(agent.state)}>
            {t(`status.agentState.${agent.state}`)}
          </Badge>
        </div>

        {skills.length > 0 && (
          <div className="flex flex-col gap-1">
            <span className="text-xs font-medium text-foreground-muted">
              {t('agents.card.capabilities')}
            </span>
            <ul className="flex flex-wrap gap-1" aria-label={t('agents.card.capabilities')}>
              {skills.map((skill) => (
                <li key={skill.id}>
                  <Badge variant="outline">{skill.name}</Badge>
                </li>
              ))}
            </ul>
          </div>
        )}

        <div className="grid grid-cols-3 gap-2 border-t border-border pt-3">
          <MetricItem
            label={t('agents.metrics.tasksCompleted.label')}
            help={t('agents.metrics.tasksCompleted.help')}
            value={metrics.tasksCompleted}
          />
          <MetricItem
            label={t('agents.metrics.approvedInReview.label')}
            help={t('agents.metrics.approvedInReview.help')}
            value={metrics.approvedInReview}
          />
          <MetricItem
            label={t('agents.metrics.rework.label')}
            help={t('agents.metrics.rework.help')}
            value={metrics.rework}
          />
        </div>

        <div className="mt-auto flex flex-col gap-2">
          {definition && (
            <Button asChild variant="ghost" className="min-h-11 w-full">
              <Link to={`/orchestrator?tab=definitions&definition=${definition.id}`}>
                <Settings aria-hidden="true" className="size-4" />
                {t('agents.card.configureDefinition')}
              </Link>
            </Button>
          )}
          <Button
            type="button"
            variant="outline"
            className="min-h-11 w-full"
            onClick={() => onSelect(agent)}
          >
            {t('agents.card.details')}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
