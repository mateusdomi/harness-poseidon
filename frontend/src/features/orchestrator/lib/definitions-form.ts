import { z } from 'zod';

import {
  agentRoleSchema,
  type Account,
  type ActorCritic,
  type AgentDefinition,
  type AgentDefinitionState,
  type CreateAgentDefinitionInput,
  type EffortLevel,
  type Provider,
  type RiskLevel,
} from '@/api';

/**
 * Lógica do formulário de definições de agente (FR-5) — fora dos componentes
 * para manter o fast-refresh limpo e a lógica testável isoladamente.
 */

/** Estado do ciclo de vida — ausente no payload = habilitada. */
export function definitionState(definition: AgentDefinition): AgentDefinitionState {
  if (definition.archivedAt) return 'archived';
  if (definition.enabled === false) return 'disabled';
  return definition.state ?? 'enabled';
}

export const STATE_BADGE_VARIANT: Record<AgentDefinitionState, 'success' | 'warning' | 'outline'> = {
  enabled: 'success',
  disabled: 'warning',
  archived: 'outline',
};

/* ---- schema do formulário (mensagens = CHAVES i18n, traduzidas na render) ----
 * Campos de texto opcionais trafegam como string ('' = não preenchido) e
 * selects nullable como '' (= "não definido"); a conversão para null/array
 * acontece em `valuesToInput`.
 */
export const definitionFormSchema = z.object({
  key: z
    .string()
    .trim()
    .min(1, 'common.validation.required')
    .regex(/^[a-z0-9]+(?:-[a-z0-9]+)*$/, 'common.validation.slug'),
  name: z.string().trim().min(2, 'common.validation.min2'),
  role: agentRoleSchema,
  specialty: z.string(),
  description: z.string(),
  persona: z.string(),
  mission: z.string(),
  responsibilities: z.string(),
  instructions: z.string(),
  restrictions: z.string(),
  bestPractices: z.string(),
  skillIds: z.array(z.string()),
  toolIds: z.array(z.string()),
  stacksText: z.string(),
  defaultModelId: z.string(),
  defaultEffort: z.string(),
  preferredAccountId: z.string(),
  fallbackModelIds: z.array(z.string()),
  team: z.string(),
  actorCritic: z.string(),
  risk: z.string(),
});
export type DefinitionFormValues = z.infer<typeof definitionFormSchema>;

export const EMPTY_DEFINITION_VALUES: DefinitionFormValues = {
  key: '',
  name: '',
  role: 'specialist',
  specialty: '',
  description: '',
  persona: '',
  mission: '',
  responsibilities: '',
  instructions: '',
  restrictions: '',
  bestPractices: '',
  skillIds: [],
  toolIds: [],
  stacksText: '',
  defaultModelId: '',
  defaultEffort: '',
  preferredAccountId: '',
  fallbackModelIds: [],
  team: '',
  actorCritic: '',
  risk: '',
};

/** Definição existente → valores do formulário (null vira ''). */
export function definitionToFormValues(definition: AgentDefinition): DefinitionFormValues {
  return {
    key: definition.key,
    name: definition.name,
    role: definition.role,
    specialty: definition.specialty ?? '',
    description: definition.description,
    persona: definition.persona ?? '',
    mission: definition.mission ?? '',
    responsibilities: definition.responsibilities ?? (definition.deliverables ?? []).join('\n'),
    instructions: definition.instructions ?? (definition.operatingPrinciples ?? []).join('\n'),
    restrictions: definition.restrictions ?? (definition.limitations ?? []).join('\n'),
    bestPractices: definition.bestPractices ?? (definition.qualityCriteria ?? []).join('\n'),
    skillIds: definition.skillIds,
    toolIds: definition.toolIds,
    stacksText: (definition.stacks ?? []).join(', '),
    defaultModelId: definition.defaultModelId ?? '',
    defaultEffort: definition.defaultEffort ?? '',
    preferredAccountId: definition.preferredAccountId ?? '',
    fallbackModelIds: definition.fallbackModelIds ?? [],
    team: definition.team ?? '',
    actorCritic: definition.actorCritic ?? '',
    risk: definition.risk ?? '',
  };
}

const emptyToNull = (value: string) => (value.trim() === '' ? null : value.trim());

/** Valores do formulário → input do comando (string vazia vira null). */
export function valuesToInput(values: DefinitionFormValues): CreateAgentDefinitionInput {
  return {
    key: values.key.trim(),
    name: values.name.trim(),
    role: values.role,
    specialty: emptyToNull(values.specialty),
    description: values.description.trim(),
    persona: emptyToNull(values.persona),
    mission: emptyToNull(values.mission),
    responsibilities: emptyToNull(values.responsibilities),
    instructions: emptyToNull(values.instructions),
    restrictions: emptyToNull(values.restrictions),
    bestPractices: emptyToNull(values.bestPractices),
    skillIds: values.skillIds,
    toolIds: values.toolIds,
    stacks: values.stacksText
      .split(',')
      .map((stack) => stack.trim())
      .filter((stack) => stack !== ''),
    defaultModelId: values.defaultModelId === '' ? null : values.defaultModelId,
    defaultEffort: values.defaultEffort === '' ? null : (values.defaultEffort as EffortLevel),
    preferredAccountId: values.preferredAccountId === '' ? null : values.preferredAccountId,
    fallbackModelIds: values.fallbackModelIds,
    team: emptyToNull(values.team),
    actorCritic: values.actorCritic === '' ? null : (values.actorCritic as ActorCritic),
    risk: values.risk === '' ? null : (values.risk as RiskLevel),
  };
}

/** Rótulo "Provider — rótulo" da conta preferencial no select. */
export function accountOptionLabel(account: Account, providers: Provider[]): string {
  const provider = providers.find((entry) => entry.id === account.providerId);
  return provider ? `${provider.name} — ${account.label}` : account.label;
}
