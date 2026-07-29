import type { ChatTurnEffort, Model } from '@/api';

export const BUSINESS_WORK_PROFILES = [
  'quick',
  'balanced',
  'analytical',
  'maximumDepth',
] as const;
export type BusinessWorkProfile = (typeof BUSINESS_WORK_PROFILES)[number];

export const BUSINESS_DEDICATION_LEVELS = ['essential', 'complete', 'deep'] as const;
export type BusinessDedicationLevel = (typeof BUSINESS_DEDICATION_LEVELS)[number];

export interface BusinessTurnSelection {
  modelId: string;
  effort: ChatTurnEffort;
}

const DEDICATION_EFFORT: Record<BusinessDedicationLevel, ChatTurnEffort> = {
  essential: 'low',
  complete: 'medium',
  deep: 'high',
};

function eligibleChatModels(models: readonly Model[]): Model[] {
  return models.filter((model) => model.enabled && model.capabilities.includes('chat'));
}

function lowestKnownCost(models: readonly Model[]): Model | undefined {
  return models
    .filter(
      (model) =>
        model.costPer1kInputUsd !== null &&
        model.costPer1kOutputUsd !== null,
    )
    .sort(
      (left, right) =>
        left.costPer1kInputUsd! +
          left.costPer1kOutputUsd! -
          (right.costPer1kInputUsd! + right.costPer1kOutputUsd!) ||
        right.contextWindow - left.contextWindow ||
        left.id.localeCompare(right.id),
    )[0];
}

function largestContext(models: readonly Model[]): Model | undefined {
  return [...models].sort(
    (left, right) =>
      right.contextWindow - left.contextWindow || left.id.localeCompare(right.id),
  )[0];
}

/**
 * De/para central da experiência de negócio. A UI não conhece provider nem
 * nomes de modelos: escolhe por capacidades publicadas no catálogo. O perfil
 * equilibrado preserva o roteamento padrão do backend.
 */
export function resolveBusinessTurnSelection(
  models: readonly Model[],
  profile: BusinessWorkProfile,
  dedication: BusinessDedicationLevel,
): BusinessTurnSelection {
  const eligible = eligibleChatModels(models);
  const selected =
    profile === 'quick'
      ? lowestKnownCost(eligible)
      : profile === 'analytical'
        ? largestContext(eligible)
        : profile === 'maximumDepth'
          ? largestContext(
              eligible.filter((model) =>
                model.effortMappings.some((mapping) => mapping.effort === 'max'),
              ),
            ) ?? largestContext(eligible)
          : undefined;
  const requestedEffort =
    profile === 'maximumDepth' &&
    selected?.effortMappings.some((mapping) => mapping.effort === 'max')
      ? 'max'
      : DEDICATION_EFFORT[dedication];
  const supportedEfforts = selected?.effortMappings.map((mapping) => mapping.effort) ?? [];
  const effort =
    selected && supportedEfforts.length > 0 && !supportedEfforts.includes(requestedEffort)
      ? supportedEfforts.includes('medium')
        ? 'medium'
        : supportedEfforts[0]
      : requestedEffort;

  return { modelId: selected?.id ?? '', effort };
}
