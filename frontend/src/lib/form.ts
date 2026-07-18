import type { FieldErrors, FieldValues, Resolver } from 'react-hook-form';
import type { z } from 'zod';

/**
 * Resolver minimalista Zod → react-hook-form.
 * O projeto não depende de `@hookform/resolvers`; este resolver cobre o
 * necessário: erros por path (aninhados, ex. `brand.primaryColor`) e
 * mensagens como CHAVES i18n, traduzidas na camada de formulário.
 */
export function zodResolver<T extends FieldValues>(schema: z.ZodType<T>): Resolver<T> {
  return async (values) => {
    const result = schema.safeParse(values);
    if (result.success) {
      return { values: result.data, errors: {} };
    }
    const errors: Record<string, unknown> = {};
    for (const issue of result.error.issues) {
      const segments = issue.path.length > 0 ? issue.path : ['root'];
      let node = errors;
      for (let index = 0; index < segments.length - 1; index += 1) {
        const segment = String(segments[index]);
        node[segment] = (node[segment] as Record<string, unknown> | undefined) ?? {};
        node = node[segment] as Record<string, unknown>;
      }
      const leaf = String(segments[segments.length - 1]);
      if (!node[leaf]) {
        node[leaf] = { type: issue.code, message: issue.message };
      }
    }
    return { values: {}, errors: errors as FieldErrors<T> };
  };
}
