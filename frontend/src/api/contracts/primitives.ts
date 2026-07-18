import { z } from 'zod';

/**
 * Primitivas do contrato Poseidon — idênticas às do backend (.NET).
 * Base `/api/v1`, JSON camelCase, IDs ULID, datas UTC ISO-8601 em string.
 */

/** ULID como string (26 chars Crockford Base32, ex.: `01J9QH...`). */
export const ulidSchema = z
  .string()
  .regex(/^[0-9A-HJKMNP-TV-Z]{26}$/i, 'ULID inválido')
  .transform((value) => value.toUpperCase());
export type Ulid = z.infer<typeof ulidSchema>;

/** Data/hora UTC em ISO-8601 (string), ex.: `2026-07-17T12:00:00Z`. */
export const isoDateTimeSchema = z.string().datetime({ offset: true });
export type IsoDateTime = z.infer<typeof isoDateTimeSchema>;

/** Parâmetros de paginação por cursor (`?cursor=&limit=`). */
export const cursorQuerySchema = z.object({
  cursor: z.string().optional(),
  limit: z.number().int().min(1).max(200).optional(),
});
export type CursorQuery = z.infer<typeof cursorQuerySchema>;

/** Filtro de listagem: igualdade por campo (serializado como query string no HTTP). */
export type ListFilter = Record<string, string | number | boolean | undefined>;

export interface ListQuery extends CursorQuery {
  filter?: ListFilter;
}

/** Página de resultados por cursor: `{ items, nextCursor }`. */
export interface Page<T> {
  items: T[];
  /** `null` quando não há próxima página. */
  nextCursor: string | null;
}

export function pageSchema<T extends z.ZodTypeAny>(itemSchema: T) {
  return z.object({
    items: z.array(itemSchema),
    nextCursor: z.string().nullable(),
  });
}

/** RFC 7807 `application/problem+json`. */
export const problemDetailsSchema = z.object({
  type: z.string(),
  title: z.string(),
  status: z.number().int(),
  detail: z.string().optional(),
  /** Erros de validação por campo, quando aplicável. */
  errors: z.record(z.array(z.string())).optional(),
});
export type ProblemDetails = z.infer<typeof problemDetailsSchema>;

/** Erro lançado pelos clientes (HTTP e mock) carregando o problem+json. */
export class ApiError extends Error {
  readonly problem: ProblemDetails;

  constructor(problem: ProblemDetails) {
    super(problem.detail ?? problem.title);
    this.name = 'ApiError';
    this.problem = problem;
  }

  static of(status: number, title: string, detail?: string, errors?: Record<string, string[]>) {
    return new ApiError({
      type: `https://httpstatuses.com/${status}`,
      title,
      status,
      ...(detail !== undefined ? { detail } : {}),
      ...(errors !== undefined ? { errors } : {}),
    });
  }
}

/** Progresso em TRÊS trilhas sempre separadas — nunca somar. */
export const progressTrackSchema = z.enum(['executed', 'validated', 'approved']);
export type ProgressTrack = z.infer<typeof progressTrackSchema>;

const trackValueSchema = z.number().min(0).max(100);

/** Percentuais 0–100 por trilha: executado / validado / aprovado. */
export const progressSchema = z.object({
  executed: trackValueSchema,
  validated: trackValueSchema,
  approved: trackValueSchema,
});
export type Progress = z.infer<typeof progressSchema>;
