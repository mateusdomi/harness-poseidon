import { z } from 'zod';

import { agentRoleSchema } from '@/api';
import {
  EMPTY_DEFINITION_VALUES,
  type DefinitionFormValues,
} from '@/features/orchestrator/lib/definitions-form';

/**
 * Importação/template de definição de agente (§16.8).
 *
 * Só aceitamos os campos que o contrato de definição realmente possui, e
 * NENHUM segredo: referências de credencial pertencem à conta do provider,
 * nunca a um arquivo de definição. Campos desconhecidos são rejeitados
 * explicitamente (não ignorados em silêncio) para que o usuário veja o erro.
 */

/** Campos aceitos no arquivo — espelham `DefinitionFormValues` (sem ids). */
export const definitionImportSchema = z
  .object({
    key: z.string().optional(),
    name: z.string().optional(),
    role: agentRoleSchema.optional(),
    specialty: z.string().optional(),
    description: z.string().optional(),
    team: z.string().optional(),
    persona: z.string().optional(),
    mission: z.string().optional(),
    responsibilities: z.string().optional(),
    instructions: z.string().optional(),
    restrictions: z.string().optional(),
    bestPractices: z.string().optional(),
    stacks: z.array(z.string()).optional(),
  })
  .strict();

export type DefinitionImport = z.infer<typeof definitionImportSchema>;

/** Chaves que jamais podem vir de um arquivo (segredo ou vínculo de conta). */
const FORBIDDEN_KEYS = [
  'apiKey',
  'token',
  'secret',
  'credential',
  'credentialRef',
  'password',
  'preferredAccountId',
];

export interface ImportIssue {
  /** Campo do formulário, quando o erro é atribuível a um campo. */
  field: string | null;
  /** Chave i18n da mensagem. */
  messageKey: string;
  /** Complemento já legível (ex.: nome do campo desconhecido). */
  detail?: string;
}

export interface ImportResult {
  ok: boolean;
  /** Valores prontos para popular o formulário (merge sobre os vazios). */
  values: DefinitionFormValues | null;
  issues: ImportIssue[];
}

/**
 * Converte o conteúdo de um arquivo JSON em valores do formulário.
 * Erros são reportados por campo; nada é aplicado quando há erro.
 */
export function parseDefinitionImport(raw: string): ImportResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return {
      ok: false,
      values: null,
      issues: [{ field: null, messageKey: 'orchestrator.definitions.import.errors.invalidJson' }],
    };
  }

  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    return {
      ok: false,
      values: null,
      issues: [{ field: null, messageKey: 'orchestrator.definitions.import.errors.notAnObject' }],
    };
  }

  // Segredo em template/import é sempre erro — nunca "limpamos" silenciosamente.
  const present = Object.keys(parsed as Record<string, unknown>);
  const forbidden = present.filter((key) =>
    FORBIDDEN_KEYS.some((entry) => entry.toLowerCase() === key.toLowerCase()),
  );
  if (forbidden.length > 0) {
    return {
      ok: false,
      values: null,
      issues: forbidden.map((key) => ({
        field: key,
        messageKey: 'orchestrator.definitions.import.errors.forbiddenField',
        detail: key,
      })),
    };
  }

  const result = definitionImportSchema.safeParse(parsed);
  if (!result.success) {
    return {
      ok: false,
      values: null,
      issues: result.error.issues.map((issue) => ({
        field: issue.path.join('.') || null,
        messageKey:
          issue.code === 'unrecognized_keys'
            ? 'orchestrator.definitions.import.errors.unknownField'
            : 'orchestrator.definitions.import.errors.invalidField',
        detail:
          issue.code === 'unrecognized_keys'
            ? (issue as z.ZodIssue & { keys: string[] }).keys.join(', ')
            : issue.path.join('.'),
      })),
    };
  }

  const data = result.data;
  return {
    ok: true,
    issues: [],
    values: {
      ...EMPTY_DEFINITION_VALUES,
      key: data.key ?? '',
      name: data.name ?? '',
      role: data.role ?? 'specialist',
      specialty: data.specialty ?? '',
      description: data.description ?? '',
      team: data.team ?? '',
      persona: data.persona ?? '',
      mission: data.mission ?? '',
      responsibilities: data.responsibilities ?? '',
      instructions: data.instructions ?? '',
      restrictions: data.restrictions ?? '',
      bestPractices: data.bestPractices ?? '',
      stacksText: (data.stacks ?? []).join(', '),
    },
  };
}

/**
 * Modelo de arquivo para download (§16.8). Contém apenas campos de definição
 * — nenhum segredo, nenhum id de conta.
 */
export function definitionTemplateJson(): string {
  const template: DefinitionImport = {
    key: 'revisor-backend',
    name: 'Revisor backend',
    role: 'specialist',
    specialty: 'Revisão de código',
    description: 'Revisa mudanças de backend antes do merge.',
    team: 'Plataforma',
    persona: 'Engenheiro sênior, direto e objetivo, focado em risco.',
    mission: 'Garantir que mudanças de backend entrem seguras e testadas.',
    responsibilities: 'Revisar diffs\nApontar riscos de regressão\nExigir testes',
    instructions: 'Priorize correção sobre estilo. Cite arquivo e linha.',
    restrictions: 'Não aprovar mudança sem teste. Não alterar contrato público.',
    bestPractices: 'Comentários acionáveis, com exemplo de correção.',
    stacks: ['.NET', 'SQL'],
  };
  return `${JSON.stringify(template, null, 2)}\n`;
}
