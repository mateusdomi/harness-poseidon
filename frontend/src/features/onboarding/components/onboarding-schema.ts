import { z } from 'zod';

import { themeSchema } from '@/api';
import { SUPPORTED_LANGUAGES } from '@/i18n';

/**
 * Schema do wizard de primeiro uso. Mensagens de erro são CHAVES i18n —
 * traduzidas na renderização (`t(message)`).
 */
export const onboardingFormSchema = z.object({
  displayName: z.string().trim().min(2, 'common.validation.min2'),
  email: z.union([z.literal(''), z.string().trim().email('common.validation.email')]),
  language: z.enum(SUPPORTED_LANGUAGES),
  theme: themeSchema,
  workingDirectory: z.string().trim(),
  unsafeAccepted: z.literal(true, {
    errorMap: () => ({ message: 'onboarding.wizard.safety.required' }),
  }),
});
export type OnboardingFormValues = z.infer<typeof onboardingFormSchema>;

export const ONBOARDING_STEPS = ['profile', 'preferences', 'workspace', 'safety'] as const;
export type OnboardingStep = (typeof ONBOARDING_STEPS)[number];

/** Campos validados ao avançar de cada etapa. */
export const ONBOARDING_STEP_FIELDS: Record<OnboardingStep, (keyof OnboardingFormValues)[]> = {
  profile: ['displayName', 'email'],
  preferences: ['language', 'theme'],
  workspace: ['workingDirectory'],
  safety: ['unsafeAccepted'],
};
