import { z } from 'zod';

import { themeSchema } from '@/api';
import { SUPPORTED_LANGUAGES } from '@/i18n';

/**
 * Schema do wizard de primeiro uso. Mensagens de erro são CHAVES i18n —
 * traduzidas na renderização (`t(message)`).
 */
const protectedOnboardingSchema = z.object({
  displayName: z.string().trim().min(2, 'common.validation.min2'),
  email: z.union([z.literal(''), z.string().trim().email('common.validation.email')]),
  language: z.enum(SUPPORTED_LANGUAGES),
  theme: themeSchema,
  workingDirectory: z.string().trim(),
  unsafeAccepted: z.boolean().optional(),
});

/** Schema estrito (ambiente sem proteção de isolamento): o aceite explícito é obrigatório. */
export const onboardingFormSchema = protectedOnboardingSchema.extend({
  unsafeAccepted: z.literal(true, {
    errorMap: () => ({ message: 'onboarding.wizard.safety.required' }),
  }),
});
export type OnboardingFormValues = z.infer<typeof protectedOnboardingSchema>;

/**
 * Schema efetivo conforme a proteção REAL do ambiente. Com isolamento operante (Docker + imagem),
 * não há risco a aceitar e o aceite do modo inseguro deixa de ser exigido; sem ele — ou enquanto o
 * estado não é conhecido — o aceite explícito continua obrigatório (fail-closed).
 */
export function onboardingSchemaFor(isolationProtected: boolean) {
  return isolationProtected ? protectedOnboardingSchema : onboardingFormSchema;
}

export const ONBOARDING_STEPS = ['profile', 'preferences', 'workspace', 'safety'] as const;
export type OnboardingStep = (typeof ONBOARDING_STEPS)[number];

/** Campos validados ao avançar de cada etapa. */
export const ONBOARDING_STEP_FIELDS: Record<OnboardingStep, (keyof OnboardingFormValues)[]> = {
  profile: ['displayName', 'email'],
  preferences: ['language', 'theme'],
  workspace: ['workingDirectory'],
  safety: ['unsafeAccepted'],
};
