import type { AgentDefinition } from '@/api';
import { resolveAgentIdentity } from '@/lib/agent-persona';

export interface PublicDefinitionCopy {
  name: string;
  roleLabel: string;
  specialty: string;
  team: string | null;
  description: string;
  persona: string;
  mission: string;
  responsibilities: string[];
  instructions: string[];
  restrictions: string[];
  bestPractices: string[];
  localized: boolean;
}

const TEAM_PT: Record<string, string> = {
  Architecture: 'Arquitetura',
  Delivery: 'Entregas',
  Documentation: 'Documentação',
  Engineering: 'Engenharia',
  Orchestration: 'Orquestração',
  Product: 'Produto',
  Quality: 'Qualidade',
};

function split(value: string | null | undefined, fallback: readonly string[]): string[] {
  const text = value?.trim();
  return text
    ? text.split(/\r?\n|;|,/).map((item) => item.trim()).filter(Boolean)
    : fallback.map((item) => item.trim()).filter(Boolean);
}

/**
 * Projeção pública em PT-BR das definições canônicas. O registro técnico
 * permanece intacto para edição e para o runtime; a UI pública não expõe
 * conteúdo inglês como se fosse texto final do produto.
 */
export function publicDefinitionCopy(
  definition: AgentDefinition,
  language: string,
): PublicDefinitionCopy {
  const identity = resolveAgentIdentity(definition.key, definition.name);
  if (!language.toLowerCase().startsWith('pt')) {
    return {
      name: identity.humanName,
      roleLabel: identity.roleLabel ?? definition.name,
      specialty: definition.specialty ?? identity.roleLabel ?? definition.name,
      team: definition.team ?? null,
      description: definition.description,
      persona: definition.persona ?? '',
      mission: definition.mission ?? '',
      responsibilities: split(definition.responsibilities, definition.deliverables ?? []),
      instructions: split(definition.instructions, definition.operatingPrinciples ?? []),
      restrictions: split(definition.restrictions, definition.limitations ?? []),
      bestPractices: split(definition.bestPractices, definition.qualityCriteria ?? []),
      localized: false,
    };
  }

  const role = identity.roleLabel ?? 'especialista';
  const leadership = definition.role === 'chief';
  return {
    name: identity.humanName,
    roleLabel: role,
    specialty: role,
    team: definition.team ? (TEAM_PT[definition.team] ?? definition.team) : null,
    description: leadership
      ? 'Coordena a entrega, delega o trabalho aos especialistas, aplica os gates e comunica o progresso com rastreabilidade.'
      : `Atua em ${role.toLocaleLowerCase('pt-BR')}, dentro do escopo e das permissões definidos para a entrega.`,
    persona: leadership
      ? 'Liderança pragmática, transparente e orientada a decisões, evidências e segurança operacional.'
      : `Profissional de ${role.toLocaleLowerCase('pt-BR')}, criterioso com evidências, limites de escopo e qualidade.`,
    mission: leadership
      ? 'Transformar a intenção do usuário em resultados entregues e aprovados, coordenando a fleet sem assumir o trabalho dos especialistas.'
      : `Executar o trabalho de ${role.toLocaleLowerCase('pt-BR')} com resultado verificável, rastreável e aderente aos critérios de aceite.`,
    responsibilities: leadership
      ? [
          'Triar solicitações e decompor demandas em trabalho verificável.',
          'Delegar cada parte ao especialista adequado e acompanhar o fluxo.',
          'Aplicar gates, registrar decisões e comunicar bloqueios com honestidade.',
        ]
      : [
          `Analisar as demandas de ${role.toLocaleLowerCase('pt-BR')} atribuídas à identidade.`,
          'Produzir os entregáveis previstos com evidências rastreáveis.',
          'Sinalizar bloqueios, riscos e dúvidas sem inventar informações.',
        ],
    instructions: [
      'Respeitar o escopo, os claims, as regras do repositório e os gates aplicáveis.',
      'Registrar evidências antes de declarar o trabalho concluído.',
    ],
    restrictions: leadership
      ? [
          'Não executar diretamente o trabalho de um especialista quando houver uma identidade adequada.',
          'Não aprovar os próprios gates nem substituir decisões humanas.',
        ]
      : [
          'Não ultrapassar o escopo ou as permissões da tarefa.',
          'Não aprovar o próprio trabalho nem mascarar dados ausentes.',
        ],
    bestPractices: [
      'Comunicar de forma clara, objetiva e vinculada às fontes.',
      'Preservar trabalho não relacionado e manter alterações recuperáveis.',
    ],
    localized: true,
  };
}
