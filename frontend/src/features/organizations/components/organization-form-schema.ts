import { z } from 'zod';

import { brandSchema } from '@/api';

/**
 * Mensagens de erro são CHAVES i18n, traduzidas na renderização.
 *
 * O plano NÃO é escolhido pelo usuário comum (§8): é derivado da licença e
 * mostrado como read-only. Por isso não faz parte do formulário — omiti-lo na
 * criação deixa o backend aplicar o padrão; omiti-lo na edição preserva o
 * valor atual. A ausência de um contrato de plano/capabilities está registrada
 * em `docs/frontend/HANDOFF_API.md`.
 */
export const organizationFormSchema = z.object({
  name: z.string().trim().min(2, 'common.validation.min2'),
  slug: z
    .string()
    .trim()
    .regex(/^[a-z0-9]+(?:-[a-z0-9]+)*$/, 'common.validation.slug'),
  brand: brandSchema,
});
export type OrganizationFormValues = z.infer<typeof organizationFormSchema>;
