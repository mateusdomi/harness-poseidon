import { useTranslation } from 'react-i18next';

import type {
  Account,
  AgentDefinition,
  Model,
  Provider,
  Skill,
  Tool,
} from '@/api';
import { Badge } from '@/design-system';
import { formatDateTime } from '@/lib/format';
import {
  accountOptionLabel,
  definitionState,
  STATE_BADGE_VARIANT,
} from '@/features/orchestrator/lib/definitions-form';
import { ModalDialog } from '@/features/shared/components/modal-dialog';

export interface DefinitionDetailsDialogProps {
  definition: AgentDefinition;
  skills: Skill[];
  tools: Tool[];
  models: Model[];
  accounts: Account[];
  providers: Provider[];
  onClose: () => void;
}

const EMPTY = '—';
const joinNames = (names: string[]) => (names.length > 0 ? names.join(', ') : EMPTY);

/**
 * Visualização read-only da definição: TODOS os campos (identidade,
 * comportamento e execução), a versão atual e o histórico de revisões.
 */
export function DefinitionDetailsDialog({
  definition,
  skills,
  tools,
  models,
  accounts,
  providers,
  onClose,
}: DefinitionDetailsDialogProps) {
  const { t, i18n } = useTranslation();
  const title = t('orchestrator.definitions.details.title', { name: definition.name });
  const state = definitionState(definition);

  const model = models.find((entry) => entry.id === definition.defaultModelId) ?? null;
  const account = accounts.find((entry) => entry.id === definition.preferredAccountId) ?? null;
  const provider = model
    ? (providers.find((entry) => entry.id === model.providerId) ?? null)
    : account
      ? (providers.find((entry) => entry.id === account.providerId) ?? null)
      : null;

  const skillNames = definition.skillIds
    .map((id) => skills.find((skill) => skill.id === id)?.name)
    .filter((name): name is string => Boolean(name));
  const toolNames = definition.toolIds
    .map((id) => tools.find((tool) => tool.id === id)?.name)
    .filter((name): name is string => Boolean(name));
  const fallbackNames = (definition.fallbackModelIds ?? [])
    .map((id) => models.find((entry) => entry.id === id)?.displayName)
    .filter((name): name is string => Boolean(name));

  const textFields = [
    ['key', definition.key],
    ['role', t(`orchestrator.definitions.role.${definition.role}`)],
    ['specialty', definition.specialty],
    ['description', definition.description || null],
    ['persona', definition.persona],
    ['mission', definition.mission],
    ['responsibilities', definition.responsibilities ?? joinNames(definition.deliverables ?? [])],
    ['instructions', definition.instructions ?? joinNames(definition.operatingPrinciples ?? [])],
    ['restrictions', definition.restrictions ?? joinNames(definition.limitations ?? [])],
    ['bestPractices', definition.bestPractices ?? joinNames(definition.qualityCriteria ?? [])],
    ['stacks', joinNames(definition.stacks ?? [])],
    ['defaultModel', model?.displayName ?? null],
    [
      'defaultEffort',
      definition.defaultEffort
        ? t(`orchestrator.definitions.effort.${definition.defaultEffort}`)
        : null,
    ],
    ['preferredAccount', account ? accountOptionLabel(account, providers) : null],
    ['fallbackModels', joinNames(fallbackNames)],
    ['team', definition.team],
    [
      'actorCritic',
      definition.actorCritic
        ? t(`orchestrator.definitions.actorCritic.${definition.actorCritic}`)
        : null,
    ],
    ['risk', definition.risk ? t(`orchestrator.definitions.risk.${definition.risk}`) : null],
    ['skills', joinNames(skillNames)],
    ['tools', joinNames(toolNames)],
  ] as const;

  const history = [...(definition.history ?? [])].reverse();

  return (
    <ModalDialog label={title} onClose={onClose} className="max-w-3xl">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="font-heading text-lg font-semibold">{definition.name}</h2>
        <Badge variant={STATE_BADGE_VARIANT[state]}>
          {t(`orchestrator.definitions.state.${state}`)}
        </Badge>
        <Badge variant="outline">
          {t('orchestrator.definitions.details.version', { version: definition.version ?? 1 })}
        </Badge>
        {provider ? <Badge variant="info">{provider.name}</Badge> : null}
      </div>

      <dl className="flex flex-col gap-3">
        {textFields.map(([field, value]) => (
          <div key={field} className="flex flex-col gap-0.5">
            <dt className="text-xs font-medium text-foreground-muted">
              {t(`orchestrator.definitions.fields.${field}`)}
            </dt>
            <dd className="whitespace-pre-wrap text-sm">{value ?? EMPTY}</dd>
          </div>
        ))}
      </dl>

      <section
        aria-labelledby="definition-history-title"
        className="flex flex-col gap-2 border-t border-border pt-4"
      >
        <h3 id="definition-history-title" className="text-sm font-semibold">
          {t('orchestrator.definitions.details.historyTitle')}
        </h3>
        {history.length === 0 ? (
          <p className="text-sm text-foreground-muted">
            {t('orchestrator.definitions.details.historyEmpty')}
          </p>
        ) : (
          <ul className="flex flex-col gap-1">
            {history.map((entry) => (
              <li
                key={entry.version}
                className="flex flex-wrap items-center gap-2 text-xs text-foreground-muted"
              >
                <Badge variant="outline">
                  {t('orchestrator.definitions.details.version', { version: entry.version })}
                </Badge>
                <span>{formatDateTime(entry.changedAt, i18n.language)}</span>
                <span>{entry.summary}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </ModalDialog>
  );
}
