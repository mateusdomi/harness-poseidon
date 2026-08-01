/**
 * Humanização (camada de APRESENTAÇÃO) das personas e das contas de execução.
 *
 * O dono quer que a UI simule uma EQUIPE DE TI REAL: cada persona/conta ganha um
 * NOME HUMANO brasileiro e um AVATAR. Os aliases técnicos (`chief-orchestrator`,
 * `chief-claude-primary`, "Chefe — <projeto>", …) permanecem INTACTOS na lógica,
 * nos contratos e no backend — só a exibição muda. O alias original continua
 * visível como subtítulo/tooltip por transparência.
 *
 * Determinístico e SEM REDE: o avatar é derivado por hash do nome (iniciais +
 * cor HSL estável). Nunca há URL externa nem serviço de terceiros — respeita o
 * CSP e funciona offline.
 */

/** Nome humano + rótulo de papel em pt-BR de uma identidade de agente. */
export interface PersonaName {
  /** Nome humano em destaque (ex.: "Bruna Magalhães"). */
  humanName: string;
  /** Papel/persona amigável em pt-BR (ex.: "Orquestrador"). */
  roleLabel: string;
}

/**
 * Mapa determinístico alias → (nome humano, papel).
 *
 * Cobre três dimensões, todas mapeadas pelo alias técnico canônico:
 *  1. Personas canônicas (`docs/agents/*.yaml` → `key`): chief-orchestrator,
 *     critic-qa, product-requirements-analyst, software-architect,
 *     software-engineer, technical-writer, delivery-*, architecture-*.
 *  2. Chaves das definições do frontend (fixtures/contrato `AgentDefinition.key`):
 *     chief, backend-engineer, frontend-engineer, reviewer, tester, designer,
 *     security.
 *  3. As 7 contas de execução da fleet (`AgentAccountRoster.alias`).
 *
 * Nomes humanos são reaproveitados entre dimensões de propósito (ex.: a conta
 * `chief-claude-primary` compartilha "Bruna Magalhães" com a persona chief) para
 * que a equipe pareça coesa — a mesma "pessoa" veste a persona e roda a conta.
 */
export const PERSONA_NAMES: Readonly<Record<string, PersonaName>> = {
  /* ---- Personas canônicas (docs/agents/*.yaml) ---- */
  'chief-orchestrator': {
    humanName: 'Bruna Magalhães',
    roleLabel: 'Diretora de Engenharia',
  },
  'critic-qa': { humanName: 'Beatriz Nunes', roleLabel: 'Crítica / QA' },
  'product-requirements-analyst': { humanName: 'Camila Rocha', roleLabel: 'Análise de Requisitos' },
  'software-architect': { humanName: 'Henrique Barros', roleLabel: 'Arquiteto de Software' },
  'software-engineer': { humanName: 'Lucas Ferreira', roleLabel: 'Engenheiro de Software' },
  'technical-writer': { humanName: 'Marina Alves', roleLabel: 'Redação Técnica' },

  /* ---- Personas de Delivery (delivery-*) ---- */
  'delivery-benefits-analyst': { humanName: 'Patrícia Gomes', roleLabel: 'Análise de Benefícios' },
  'delivery-daily-intelligence': { humanName: 'Diego Martins', roleLabel: 'Inteligência Diária' },
  'delivery-documentation-steward': {
    humanName: 'Sofia Ribeiro',
    roleLabel: 'Curadoria de Documentação',
  },
  'delivery-executive-reporting': { humanName: 'Eduardo Carvalho', roleLabel: 'Reporte Executivo' },
  'delivery-forecast-analyst': { humanName: 'Juliana Teixeira', roleLabel: 'Análise de Previsão' },
  'delivery-quality-release-auditor': {
    humanName: 'Fernando Azevedo',
    roleLabel: 'Auditoria de Qualidade e Release',
  },
  'delivery-risk-dependency-analyst': {
    humanName: 'Renata Cardoso',
    roleLabel: 'Análise de Risco e Dependências',
  },
  'delivery-tech-lead-copilot': { humanName: 'Bruno Siqueira', roleLabel: 'Tech Lead Copiloto' },

  /* ---- Personas de Arquitetura (architecture-*) ---- */
  'architecture-adr-writer': { humanName: 'André Fonseca', roleLabel: 'Redação de ADR' },
  'architecture-chief': { humanName: 'Gustavo Moraes', roleLabel: 'Arquiteto Principal' },
  'architecture-critic': { humanName: 'Larissa Pires', roleLabel: 'Crítica de Arquitetura' },
  'architecture-data': { humanName: 'Tiago Correia', roleLabel: 'Arquitetura de Dados' },
  'architecture-discovery': {
    humanName: 'Isabela Freitas',
    roleLabel: 'Descoberta de Arquitetura',
  },
  'architecture-enterprise': { humanName: 'Ricardo Salgado', roleLabel: 'Arquitetura Corporativa' },
  'architecture-infrastructure': {
    humanName: 'Marcelo Dias',
    roleLabel: 'Arquitetura de Infraestrutura',
  },
  'architecture-integration': { humanName: 'Vanessa Lima', roleLabel: 'Arquitetura de Integração' },
  'architecture-rationalization-analyst': {
    humanName: 'Otávio Ramos',
    roleLabel: 'Análise de Racionalização',
  },
  'architecture-security': {
    humanName: 'Priscila Monteiro',
    roleLabel: 'Arquitetura de Segurança',
  },
  'architecture-solution-architect': {
    humanName: 'Rodrigo Vasconcelos',
    roleLabel: 'Arquiteto de Solução',
  },

  /* ---- Chaves das definições do frontend (AgentDefinition.key nas fixtures) ---- */
  chief: {
    humanName: 'Bruna Magalhães',
    roleLabel: 'Diretora de Engenharia',
  },
  'backend-engineer': { humanName: 'Thiago Mendes', roleLabel: 'Engenheiro Backend' },
  'frontend-engineer': { humanName: 'Aline Castro', roleLabel: 'Engenheira Frontend' },
  reviewer: { humanName: 'Felipe Duarte', roleLabel: 'Revisor' },
  tester: { humanName: 'Natália Souza', roleLabel: 'Testadora' },
  designer: { humanName: 'Gabriela Pinto', roleLabel: 'Designer' },
  'prototype-designer': { humanName: 'Gabriela Pinto', roleLabel: 'Designer de Protótipos' },
  security: { humanName: 'Vinícius Braga', roleLabel: 'Segurança' },
  'security-analyst': { humanName: 'Vinícius Braga', roleLabel: 'Analista de Segurança' },

  /* ---- As 7 contas de execução da fleet (AgentAccountRoster.alias) ---- */
  'chief-claude-primary': {
    humanName: 'Bruna Magalhães',
    roleLabel: 'Diretora de Engenharia',
  },
  'worker-claude-secondary': { humanName: 'Thiago Mendes', roleLabel: 'Especialista Backend' },
  'worker-codex-frontend': { humanName: 'Aline Castro', roleLabel: 'Especialista Frontend' },
  'worker-codex-critic': { humanName: 'Felipe Duarte', roleLabel: 'Revisor / Crítico' },
  'worker-antigravity-review': { humanName: 'Larissa Pires', roleLabel: 'Revisora / Crítica' },
  'worker-glm-general': { humanName: 'Vinícius Braga', roleLabel: 'Especialista Backend' },
  'worker-kimi-ui': { humanName: 'Gabriela Pinto', roleLabel: 'Especialista Frontend' },
};

/** Cores (determinísticas) de um avatar de iniciais. */
export interface AvatarColors {
  /** Cor de fundo do círculo (HSL estável derivada da semente). */
  background: string;
  /** Cor do texto das iniciais (branco — contraste sobre o fundo escuro). */
  foreground: string;
}

/** Hash estável (djb2-like) de uma string em inteiro não-negativo. */
function hashString(value: string): number {
  let hash = 0;
  for (let index = 0; index < value.length; index += 1) {
    hash = (hash * 31 + value.charCodeAt(index)) | 0;
  }
  return Math.abs(hash);
}

/**
 * Cor do avatar derivada por hash da semente (o nome humano por padrão): a
 * mesma "pessoa" recebe sempre a mesma cor em qualquer tela. Saturação e
 * luminância fixas e escuras garantem contraste WCAG AA do texto branco em
 * todo o círculo cromático, nos temas claro e escuro. Sem rede: é só uma
 * string HSL.
 */
export function avatarColorsFor(seed: string): AvatarColors {
  const hue = hashString(seed) % 360;
  return { background: `hsl(${hue} 55% 30%)`, foreground: '#ffffff' };
}

/**
 * Iniciais para o avatar: primeira letra do primeiro e do último termo do nome
 * (ou as duas primeiras letras quando há um só termo). Acentos são removidos
 * para manter a sigla limpa.
 */
export function initialsFor(name: string): string {
  const normalized = name
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .trim();
  const parts = normalized.split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

/** Identidade humanizada e resolvida de um agente (persona ou conta). */
export interface ResolvedAgentIdentity {
  /** Nome humano em destaque. */
  humanName: string;
  /** Papel amigável em pt-BR (`null` quando o alias não está no mapa). */
  roleLabel: string | null;
  /** Alias técnico original (transparência) — nunca inventado. */
  alias: string;
  /** Iniciais do avatar, derivadas do nome humano. */
  initials: string;
  /** Cores determinísticas do avatar. */
  avatar: AvatarColors;
}

/**
 * Resolve a identidade humanizada de um alias técnico. Se o alias estiver no
 * mapa, usa o nome humano e o papel correspondentes; caso contrário, cai para
 * `fallbackName` (ex.: o nome da instância do agente) ou para o próprio alias,
 * SEM inventar um papel. O avatar é sempre determinístico a partir do nome.
 */
export function resolveAgentIdentity(
  alias: string | null | undefined,
  fallbackName?: string | null,
): ResolvedAgentIdentity {
  const key = (alias ?? '').trim();
  const mapped = PERSONA_NAMES[key];
  const humanName = mapped?.humanName ?? fallbackName?.trim() ?? key ?? '';
  return {
    humanName,
    roleLabel: mapped?.roleLabel ?? null,
    alias: key,
    initials: initialsFor(humanName),
    avatar: avatarColorsFor(mapped?.humanName ?? humanName),
  };
}
