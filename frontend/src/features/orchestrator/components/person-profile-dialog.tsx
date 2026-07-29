import { useTranslation } from 'react-i18next';
import { useQuery } from '@tanstack/react-query';

import type { Agent, AgentDefinition, Skill, Task } from '@/api';
import { useApi } from '@/app/api-context';
import { Badge } from '@/design-system';
import { agentStateVariant } from '@/lib/status';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { ManagedAgentAvatar } from '@/features/shared/components/managed-agent-avatar';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { PersonaProvenance } from '@/features/agents/components/persona-provenance';

export interface PersonProfileDialogProps {
  agent: Agent;
  definition: AgentDefinition | null;
  /** Tarefa em curso, quando houver — o que a pessoa está fazendo agora. */
  task: Task | null;
  onClose: () => void;
}

/**
 * PERFIL DE PESSOA (decisão D1).
 *
 * As sete contas não são "gerentes" de trinta agentes: elas SÃO a equipe, e as
 * especialidades são COMPETÊNCIAS que a mesma pessoa exerce conforme a tarefa. É
 * isso que este perfil mostra — quem é, o que está fazendo agora, o que sabe
 * fazer e de onde veio.
 *
 * O que NÃO aparece aqui: assinatura, modelo, custo, heartbeat. Isso é operação
 * de máquina e vive no modo Técnico, na tela de Agentes.
 */
export function PersonProfileDialog({
  agent,
  definition,
  task,
  onClose,
}: PersonProfileDialogProps) {
  const { t } = useTranslation();
  const api = useApi();
  const identity = resolveAgentIdentity(definition?.key, agent.name);

  // As competências vivem no catálogo de skills; sem definição não há o que listar.
  const skillsQuery = useQuery({
    queryKey: ['orchestrator', 'person-skills'],
    queryFn: async (): Promise<Skill[]> => (await api.list('skills')).items,
    enabled: definition !== null && definition.skillIds.length > 0,
  });
  const competencies = (definition?.skillIds ?? [])
    .map((id) => (skillsQuery.data ?? []).find((skill) => skill.id === id))
    .filter((skill): skill is Skill => skill !== undefined);

  return (
    <ModalDialog label={identity.humanName} onClose={onClose}>
      <div className="flex flex-col gap-4">
        <div className="flex items-start gap-3 pr-10">
          <ManagedAgentAvatar
            alias={definition?.key ?? agent.name}
            fallbackName={agent.name}
            roleLabel={identity.roleLabel}
            size={64}
          />
          <div className="flex min-w-0 flex-col gap-1">
            <h2 className="font-heading text-xl font-semibold">{identity.humanName}</h2>
            <p className="text-sm text-foreground-muted">{identity.roleLabel}</p>
            <div className="mt-1 flex flex-wrap items-center gap-2">
              <Badge variant={agentStateVariant(agent.state)}>
                {t(`status.agentState.${agent.state}`)}
              </Badge>
              <span className="text-xs text-foreground-muted">
                {t(`status.agentStateHint.${agent.state}`)}
              </span>
            </div>
          </div>
        </div>

        <section className="flex flex-col gap-1">
          <h3 className="text-sm font-semibold">{t('orchestrator.person.doingNow')}</h3>
          <p className="text-sm text-foreground-muted">
            {task ? task.title : t('orchestrator.person.doingNothing')}
          </p>
        </section>

        <section className="flex flex-col gap-2">
          <h3 className="text-sm font-semibold">{t('orchestrator.person.competencies')}</h3>
          {competencies.length === 0 ? (
            <p className="text-sm text-foreground-muted">
              {t('orchestrator.person.competenciesEmpty')}
            </p>
          ) : (
            <ul className="flex flex-wrap gap-2">
              {competencies.map((skill) => (
                <li key={skill.id}>
                  <Badge variant="outline">{skill.name}</Badge>
                </li>
              ))}
            </ul>
          )}
          {definition?.description ? (
            <p className="text-sm text-foreground-muted">{definition.description}</p>
          ) : null}
        </section>

        {definition ? (
          <section className="flex flex-col gap-2">
            <h3 className="text-sm font-semibold">{t('orchestrator.person.provenance')}</h3>
            <PersonaProvenance definition={definition} />
          </section>
        ) : null}
      </div>
    </ModalDialog>
  );
}
