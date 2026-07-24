import { z } from 'zod';

import { isoDateTimeSchema } from './primitives';

const wireIntegerSchema = z.coerce.number().int();

/**
 * Documentos de governança/documentação em disco (working tree) que governam
 * o comportamento do sistema. Editá-los passa a valer para o runtime/Chefe,
 * que lê a governança do disco. Backend restringe estritamente ao allowlist
 * (`governance/`, `docs/`) e bloqueia path traversal.
 */
export const governanceDocFileSchema = z.object({
  path: z.string().min(1),
  name: z.string().min(1),
  size: wireIntegerSchema.nonnegative(),
  modifiedAt: isoDateTimeSchema,
});
export type GovernanceDocFile = z.infer<typeof governanceDocFileSchema>;

export const governanceDocTreeSchema = z.object({
  files: z.array(governanceDocFileSchema),
  roots: z.array(z.string().min(1)),
});
export type GovernanceDocTree = z.infer<typeof governanceDocTreeSchema>;

export const governanceDocContentSchema = z.object({
  path: z.string().min(1),
  name: z.string().min(1),
  content: z.string(),
  size: wireIntegerSchema.nonnegative(),
  modifiedAt: isoDateTimeSchema,
});
export type GovernanceDocContent = z.infer<typeof governanceDocContentSchema>;
