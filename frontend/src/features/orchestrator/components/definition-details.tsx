import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import type { Account, AgentDefinition, Model, Provider, Skill, Tool } from '@/api';
import { Badge } from '@/design-system';
import {
  accountOptionLabel,
  definitionState,
  STATE_BADGE_VARIANT,
} from '@/features/orchestrator/lib/definitions-form';
import { AgentIdentity } from '@/features/shared/components/agent-identity';
import { ModalDialog } from '@/features/shared/components/modal-dialog';
import { formatDateTime } from '@/lib/format';
import { actorCriticVariant, agentRoleVariant, effortLevelVariant, riskLevelVariant } from '@/lib/status';

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

/**
 * Divide um campo textual em itens de lista para exibição em bullets.
 *
 * APRESENTAÇÃO apenas: o conteúdo é preservado — apenas quebramos o texto
 * corrido (por vírgula, ponto-e-vírgula ou nova linha) em itens legíveis. A
 * fonte de verdade continua sendo o campo textual do contrato; o array
 * alternativo (deliverables/operatingPrinciples/…) só é usado quando o campo
 * textual não vem preenchido, exatamente como na versão anterior.
 */
function toBullets(text: string | null | undefined, fallback: readonly string[]): string[] {
  const raw = text?.trim();
  if (raw) {
    return raw
      .split(/\r?\n|;|,/)
      .map((item) => item.trim())
      .filter(Boolean);
  }
  return fallback.map((item) => item.trim()).filter(Boolean);
}

/** Bloco de seção com título destacado (card visual de leitura). */
function SectionCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-3 rounded-lg border border-border bg-surface p-4">
      <h3 className="font-heading text-sm font-semibold">{title}</h3>
      {children}
    </section>
  );
}

/** Bloco de texto narrativo (descrição/persona/missão), preservando quebras. */
function ProseField({ label, value }: { label: string; value: string | null | undefined }) {
  const text = value?.trim();
  if (!text) return null;
  return (
    <div className="flex flex-col gap-1">
      <span className="text-xs font-medium uppercase tracking-wide text-foreground-muted">
        {label}
      </span>
      <p className="whitespace-pre-wrap text-sm leading-relaxed text-foreground">{text}</p>
    </div>
  );
}

/** Campo em lista de bullets (responsabilidades/instruções/restrições/boas práticas). */
function BulletField({ label, items }: { label: string; items: string[] }) {
  if (items.length === 0) return null;
  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-xs font-medium uppercase tracking-wide text-foreground-muted">
        {label}
      </span>
      <ul className="flex list-disc flex-col gap-1 pl-5 text-sm leading-relaxed text-foreground marker:text-foreground-muted">
        {items.map((item, index) => (
          <li key={`${index}-${item}`}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

/** Grupo de chips coloridos (stacks/skills/ferramentas) sob um rótulo. */
function ChipField({
  label,
  values,
  variant,
}: {
  label: string;
  values: string[];
  variant: 'accent' | 'info' | 'brand';
}) {
  if (values.length === 0) return null;
  return (
    <div className="flex flex-col gap-1.5">
      <span className="text-xs font-medium uppercase tracking-wide text-foreground-muted">
        {label}
      </span>
      <div className="flex flex-wrap items-center gap-1.5">
        {values.map((value) => (
          <Badge key={value} variant={variant}>
            {value}
          </Badge>
        ))}
      </div>
    </div>
  );
}

/** Linha rótulo → valor da seção de execução. */
function ValueRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <dt className="text-xs font-medium uppercase tracking-wide text-foreground-muted">{label}</dt>
      <dd className="flex flex-wrap items-center gap-1.5 text-sm text-foreground">{children}</dd>
    </div>
  );
}

/**
 * Visualização read-only da definição (persona): TODOS os campos, agora
 * organizados em SEÇÕES de leitura — identidade humanizada no topo, perfil,
 * comportamento (listas), competências (chips) e execução — mais a versão
 * atual e o histórico de revisões. Só a APRESENTAÇÃO muda: nenhum campo,
 * conteúdo ou contrato é alterado.
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

  const responsibilities = toBullets(definition.responsibilities, definition.deliverables ?? []);
  const instructions = toBullets(definition.instructions, definition.operatingPrinciples ?? []);
  const restrictions = toBullets(definition.restrictions, definition.limitations ?? []);
  const bestPractices = toBullets(definition.bestPractices, definition.qualityCriteria ?? []);
  const stacks = definition.stacks ?? [];

  const hasProfile = Boolean(
    definition.description?.trim() || definition.persona?.trim() || definition.mission?.trim(),
  );
  const hasBehavior =
    responsibilities.length > 0 ||
    instructions.length > 0 ||
    restrictions.length > 0 ||
    bestPractices.length > 0;
  const hasCompetencies = stacks.length > 0 || skillNames.length > 0 || toolNames.length > 0;

  const history = [...(definition.history ?? [])].reverse();

  return (
    <ModalDialog label={title} onClose={onClose} className="max-w-3xl">
      {/* Identidade humanizada (persona) + alias técnico da definição. */}
      <div className="flex flex-col gap-3">
        <AgentIdentity
          alias={definition.key}
          fallbackName={definition.name}
          technicalLabel={definition.name}
          size={48}
          nameClassName="text-xl"
        />
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant={STATE_BADGE_VARIANT[state]}>
            {t(`orchestrator.definitions.state.${state}`)}
          </Badge>
          <Badge variant="outline">
            {t('orchestrator.definitions.details.version', { version: definition.version ?? 1 })}
          </Badge>
          <Badge variant={agentRoleVariant(definition.role)}>
            {t(`orchestrator.definitions.role.${definition.role}`)}
          </Badge>
          {definition.actorCritic ? (
            <Badge variant={actorCriticVariant(definition.actorCritic)}>
              {t(`orchestrator.definitions.actorCritic.${definition.actorCritic}`)}
            </Badge>
          ) : null}
          {definition.risk ? (
            <Badge variant={riskLevelVariant(definition.risk)}>
              {t('orchestrator.definitions.fields.risk')}:{' '}
              {t(`orchestrator.definitions.risk.${definition.risk}`)}
            </Badge>
          ) : null}
          {provider ? <Badge variant="info">{provider.name}</Badge> : null}
        </div>
        {(definition.specialty?.trim() || definition.team?.trim()) && (
          <dl className="flex flex-wrap items-center gap-x-6 gap-y-1.5">
            {definition.specialty?.trim() ? (
              <ValueRow label={t('orchestrator.definitions.fields.specialty')}>
                {definition.specialty}
              </ValueRow>
            ) : null}
            {definition.team?.trim() ? (
              <ValueRow label={t('orchestrator.definitions.fields.team')}>
                <Badge variant="outline">{definition.team}</Badge>
              </ValueRow>
            ) : null}
          </dl>
        )}
      </div>

      {hasProfile ? (
        <SectionCard title={t('orchestrator.definitions.details.sections.profile')}>
          <ProseField
            label={t('orchestrator.definitions.fields.description')}
            value={definition.description}
          />
          <ProseField
            label={t('orchestrator.definitions.fields.persona')}
            value={definition.persona}
          />
          <ProseField
            label={t('orchestrator.definitions.fields.mission')}
            value={definition.mission}
          />
        </SectionCard>
      ) : null}

      {hasBehavior ? (
        <SectionCard title={t('orchestrator.definitions.details.sections.behavior')}>
          <BulletField
            label={t('orchestrator.definitions.fields.responsibilities')}
            items={responsibilities}
          />
          <BulletField
            label={t('orchestrator.definitions.fields.instructions')}
            items={instructions}
          />
          <BulletField
            label={t('orchestrator.definitions.fields.restrictions')}
            items={restrictions}
          />
          <BulletField
            label={t('orchestrator.definitions.fields.bestPractices')}
            items={bestPractices}
          />
        </SectionCard>
      ) : null}

      {hasCompetencies ? (
        <SectionCard title={t('orchestrator.definitions.details.sections.competencies')}>
          <ChipField
            label={t('orchestrator.definitions.fields.stacks')}
            values={stacks}
            variant="accent"
          />
          <ChipField
            label={t('orchestrator.definitions.fields.skills')}
            values={skillNames}
            variant="info"
          />
          <ChipField
            label={t('orchestrator.definitions.fields.tools')}
            values={toolNames}
            variant="brand"
          />
        </SectionCard>
      ) : null}

      <SectionCard title={t('orchestrator.definitions.details.sections.execution')}>
        <dl className="flex flex-col gap-2.5">
          <ValueRow label={t('orchestrator.definitions.fields.defaultModel')}>
            {model?.displayName ?? EMPTY}
          </ValueRow>
          <ValueRow label={t('orchestrator.definitions.fields.defaultEffort')}>
            {definition.defaultEffort ? (
              <Badge variant={effortLevelVariant(definition.defaultEffort)}>
                {t(`orchestrator.definitions.effort.${definition.defaultEffort}`)}
              </Badge>
            ) : (
              EMPTY
            )}
          </ValueRow>
          <ValueRow label={t('orchestrator.definitions.fields.preferredAccount')}>
            {account ? accountOptionLabel(account, providers) : EMPTY}
          </ValueRow>
          <ValueRow label={t('orchestrator.definitions.fields.fallbackModels')}>
            {fallbackNames.length === 0
              ? EMPTY
              : fallbackNames.map((name) => (
                  <Badge key={name} variant="outline">
                    {name}
                  </Badge>
                ))}
          </ValueRow>
        </dl>
      </SectionCard>

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
