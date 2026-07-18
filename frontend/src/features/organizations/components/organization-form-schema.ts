import { z } from 'zod';

import { brandSchema } from '@/api';

export const ORGANIZATION_PLANS = ['free', 'pro', 'enterprise'] as const;

/** Mensagens de erro são CHAVES i18n, traduzidas na renderização. */
export const organizationFormSchema = z.object({
  name: z.string().trim().min(2, 'common.validation.min2'),
  slug: z
    .string()
    .trim()
    .regex(/^[a-z0-9]+(?:-[a-z0-9]+)*$/, 'common.validation.slug'),
  plan: z.enum(ORGANIZATION_PLANS),
  brand: brandSchema,
});
export type OrganizationFormValues = z.infer<typeof organizationFormSchema>;
