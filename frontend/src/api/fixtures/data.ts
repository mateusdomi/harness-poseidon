import type {
  Account,
  Agent,
  AgentDefinition,
  Approval,
  Attempt,
  AttemptEvent,
  AuditEvent,
  Budget,
  Conversation,
  Demand,
  Document,
  DocumentVersion,
  Entitlement,
  Gate,
  License,
  McpServer,
  Message,
  Model,
  Notification,
  Organization,
  Phase,
  Plugin,
  Profile,
  Project,
  Prototype,
  Provider,
  ResourceKind,
  ResourceMap,
  RoutingPolicy,
  RunTarget,
  Settings,
  Skill,
  Solicitation,
  Task,
  TaskInstruction,
  TaskState,
  Tool,
  Ulid,
  VisualReference,
  Workflow,
  WorkflowRun,
  WorkflowTemplate,
  WorkflowVersion,
} from '../contracts';
import {
  createTickClock,
  DeterministicUlidGenerator,
  FIXTURE_SEED,
  int,
  mulberry32,
  pick,
} from './prng';

export interface FixtureData {
  meta: {
    seed: number;
    currentProfileId: Ulid;
  };
  data: { [K in ResourceKind]: ResourceMap[K][] };
}

/**
 * Constrói o dataset determinístico em pt-BR (seed fixa).
 * Cobre todos os recursos do contrato: 1 perfil, 2 organizações,
 * 2 projetos, 40 tarefas nas 8 colunas, demandas, solicitações,
 * attempts com evidências, conversas, documentos (+ versões, waiver),
 * workflow template/versão/run com fases e gates, aprovações, agentes,
 * ferramentas/skills/plugins/MCP, providers/contas/modelos/budgets/
 * política de roteamento, notificações, auditoria, run-targets,
 * settings, licença/entitlements, protótipos e referências visuais.
 */
export function buildFixtures(seed: number = FIXTURE_SEED): FixtureData {
  const random = mulberry32(seed);
  const ids = new DeterministicUlidGenerator(seed);
  const tick = createTickClock();
  const id = () => ids.next();

  /* ---- perfil, organizações ---- */

  const profile: Profile = {
    id: id(),
    displayName: 'Mateus',
    email: 'mateus@poseidon.local',
    avatarUrl: null,
    locale: 'pt-BR',
    createdAt: tick(),
    lastActiveAt: tick(),
  };
  const profileAna: Profile = {
    id: id(),
    displayName: 'Ana Souza',
    email: 'ana@poseidon.local',
    avatarUrl: null,
    locale: 'pt-BR',
    createdAt: tick(),
    lastActiveAt: tick(),
  };

  const orgPoseidon: Organization = {
    id: id(),
    name: 'Poseidon Labs',
    slug: 'poseidon-labs',
    plan: 'pro',
    brand: {
      logoUrl: null,
      primaryColor: '#7C5CFC',
      secondaryColor: '#EC4899',
      typography: 'Space Grotesk',
    },
    defaultWorkflowTemplateIds: [], // preenchido após criar o template
    templateKeys: ['prd', 'spec', 'runbook'],
    policies: [
      { key: 'review.required', description: 'Toda tarefa passa por revisão antes de done.', enabled: true },
      { key: 'deploy.manual', description: 'Deploy em produção exige aprovação humana.', enabled: true },
      { key: 'quota.alert', description: 'Alertar ao atingir 80% da cota mensal.', enabled: false },
    ],
    createdAt: tick(),
  };
  const orgPessoal: Organization = {
    id: id(),
    name: 'Laboratório Pessoal',
    slug: 'lab-pessoal',
    plan: 'free',
    brand: { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
    defaultWorkflowTemplateIds: [],
    templateKeys: ['note'],
    policies: [
      { key: 'review.required', description: 'Toda tarefa passa por revisão antes de done.', enabled: false },
    ],
    createdAt: tick(),
  };

  /* ---- providers, contas, modelos ---- */

  const providerOpenAI: Provider = {
    id: id(),
    kind: 'openai',
    name: 'OpenAI',
    baseUrl: null,
    enabled: true,
  };
  const providerAnthropic: Provider = {
    id: id(),
    kind: 'anthropic',
    name: 'Anthropic',
    baseUrl: null,
    enabled: true,
  };
  const providerOllama: Provider = {
    id: id(),
    kind: 'ollama',
    name: 'Ollama (local)',
    baseUrl: 'http://localhost:11434',
    enabled: false,
  };

  const accountOpenAI: Account = {
    id: id(),
    providerId: providerOpenAI.id,
    label: 'Conta principal',
    state: 'active',
    quotaLimitUsd: 150,
    quotaUsedUsd: 87.35,
    identity: 'billing@poseidonlabs.dev',
    plan: 'payAsYouGo',
    authentication: 'apiKey',
    health: 'healthy',
    quotaWindow: 'monthly',
    quotaResetsAt: '2026-08-01T00:00:00Z',
    capabilities: ['chat', 'code', 'reasoning', 'tools', 'vision'],
  };
  const accountAnthropic: Account = {
    id: id(),
    providerId: providerAnthropic.id,
    label: 'Conta secundária',
    state: 'active',
    quotaLimitUsd: 60,
    quotaUsedUsd: 48.1,
    identity: 'contas@poseidonlabs.dev',
    plan: 'pro',
    authentication: 'oauth',
    health: 'degraded',
    quotaWindow: 'weekly',
    quotaResetsAt: '2026-07-26T00:00:00Z',
    capabilities: ['chat', 'code', 'reasoning', 'tools'],
  };
  // Conta local (Ollama): desabilitada e sem referências — exercita
  // habilitar/desabilitar e remoção desprotegida no CRUD de contas (FR-5).
  const accountOllama: Account = {
    id: id(),
    providerId: providerOllama.id,
    label: 'Ollama deste computador',
    state: 'disabled',
    quotaLimitUsd: null,
    quotaUsedUsd: 0,
    identity: null,
    plan: 'local',
    authentication: 'local',
    health: 'unknown',
    quotaWindow: 'none',
    quotaResetsAt: null,
    capabilities: ['chat', 'code'],
  };

  const modelGpt4o: Model = {
    id: id(),
    providerId: providerOpenAI.id,
    name: 'gpt-4o',
    displayName: 'GPT-4o',
    capabilities: ['chat', 'code', 'vision'],
    contextWindow: 128_000,
    costPer1kInputUsd: 0.005,
    costPer1kOutputUsd: 0.015,
    enabled: true,
    effortMappings: [
      { effort: 'low', providerValue: 'low' },
      { effort: 'medium', providerValue: 'medium' },
      { effort: 'high', providerValue: 'high' },
      { effort: 'max', providerValue: 'xhigh' },
    ],
  };
  const modelGpt4oMini: Model = {
    id: id(),
    providerId: providerOpenAI.id,
    name: 'gpt-4o-mini',
    displayName: 'GPT-4o mini',
    capabilities: ['chat', 'code'],
    contextWindow: 128_000,
    costPer1kInputUsd: 0.00015,
    costPer1kOutputUsd: 0.0006,
    enabled: true,
    effortMappings: [
      { effort: 'low', providerValue: 'low' },
      { effort: 'medium', providerValue: 'medium' },
      { effort: 'high', providerValue: 'high' },
      { effort: 'max', providerValue: 'xhigh' },
    ],
  };
  const modelClaude: Model = {
    id: id(),
    providerId: providerAnthropic.id,
    name: 'claude-sonnet-4',
    displayName: 'Claude Sonnet 4',
    capabilities: ['chat', 'code'],
    contextWindow: 200_000,
    costPer1kInputUsd: 0.003,
    costPer1kOutputUsd: 0.015,
    enabled: true,
    effortMappings: [
      { effort: 'low', providerValue: 'low' },
      { effort: 'medium', providerValue: 'medium' },
      { effort: 'high', providerValue: 'high' },
      { effort: 'max', providerValue: 'high' },
    ],
  };
  const modelLlama: Model = {
    id: id(),
    providerId: providerOllama.id,
    name: 'llama3.1:8b',
    displayName: 'Llama 3.1 8B (local)',
    capabilities: ['chat', 'code'],
    contextWindow: 8_192,
    costPer1kInputUsd: null,
    costPer1kOutputUsd: null,
    enabled: false,
    effortMappings: [
      { effort: 'low', providerValue: 'low' },
      { effort: 'medium', providerValue: 'medium' },
      { effort: 'high', providerValue: 'high' },
      { effort: 'max', providerValue: 'high' },
    ],
  };

  /* ---- skills, ferramentas, plugins, MCP ---- */

  const skillPlanejamento: Skill = {
    id: id(),
    key: 'planning',
    name: 'Planejamento',
    description: 'Quebra de demandas em tarefas executáveis.',
    version: '1.2.0',
    state: 'enabled',
  };
  const skillRevisao: Skill = {
    id: id(),
    key: 'code-review',
    name: 'Revisão de código',
    description: 'Análise de diffs com checklist de qualidade.',
    version: '1.0.3',
    state: 'enabled',
  };
  const skillTestes: Skill = {
    id: id(),
    key: 'testing',
    name: 'Testes',
    description: 'Geração e execução de testes automatizados.',
    version: '0.9.1',
    state: 'enabled',
  };
  const skillDocs: Skill = {
    id: id(),
    key: 'docs',
    name: 'Documentação',
    description: 'Redação e atualização de documentos técnicos.',
    version: '1.1.0',
    state: 'enabled',
  };
  const skillPrototipo: Skill = {
    id: id(),
    key: 'prototyping',
    name: 'Prototipação',
    description: 'Geração de protótipos navegáveis de UI.',
    version: '0.4.0',
    state: 'disabled',
  };

  const toolShell: Tool = {
    id: id(),
    key: 'shell',
    name: 'Terminal',
    description: 'Execução de comandos no ambiente do projeto.',
    kind: 'builtin',
    state: 'enabled',
  };
  const toolFs: Tool = {
    id: id(),
    key: 'fs',
    name: 'Sistema de arquivos',
    description: 'Leitura e escrita de arquivos do workspace.',
    kind: 'builtin',
    state: 'enabled',
  };
  const toolGit: Tool = {
    id: id(),
    key: 'git',
    name: 'Git',
    description: 'Operações de versionamento (diff, commit, branch).',
    kind: 'builtin',
    state: 'enabled',
  };
  const toolWebSearch: Tool = {
    id: id(),
    key: 'web.search',
    name: 'Busca web',
    description: 'Pesquisa na internet com citação de fontes.',
    kind: 'builtin',
    state: 'enabled',
  };
  const toolGithubPr: Tool = {
    id: id(),
    key: 'github.pr',
    name: 'Pull requests (GitHub)',
    description: 'Criação e inspeção de PRs via servidor MCP.',
    kind: 'mcp',
    state: 'enabled',
  };
  const toolFigma: Tool = {
    id: id(),
    key: 'figma.export',
    name: 'Exportar Figma',
    description: 'Exporta frames do Figma como referência visual.',
    kind: 'plugin',
    state: 'error',
  };

  const pluginGithub: Plugin = {
    id: id(),
    key: 'github-integration',
    name: 'Integração GitHub',
    version: '2.1.0',
    description: 'Sincroniza issues, PRs e checks com o quadro.',
    state: 'enabled',
    providesToolIds: [toolGithubPr.id],
  };
  const pluginFigma: Plugin = {
    id: id(),
    key: 'figma-bridge',
    name: 'Ponte Figma',
    version: '0.8.2',
    description: 'Importa referências visuais de arquivos do Figma.',
    state: 'error',
    providesToolIds: [toolFigma.id],
  };

  const mcpGithub: McpServer = {
    id: id(),
    name: 'github-mcp',
    transport: 'http',
    endpoint: 'https://mcp.github.local/sse',
    state: 'enabled',
    toolCount: 6,
  };
  const mcpFs: McpServer = {
    id: id(),
    name: 'filesystem-mcp',
    transport: 'stdio',
    endpoint: 'npx -y @modelcontextprotocol/server-filesystem ~/projetos',
    state: 'disabled',
    toolCount: 4,
  };

  /* ---- definições e instâncias de agentes ---- */

  const defChefe: AgentDefinition = {
    id: id(),
    key: 'chief',
    name: 'Chefe',
    role: 'chief',
    specialty: null,
    description: 'Coordena o projeto: conversa com você e delega aos especialistas.',
    defaultModelId: modelGpt4o.id,
    skillIds: [skillPlanejamento.id, skillDocs.id],
    toolIds: [toolShell.id, toolFs.id, toolGit.id, toolWebSearch.id],
    state: 'enabled',
    persona: 'Coordenador pragmático, comunica-se de forma direta e transparente.',
    mission: 'Entregar o escopo do projeto com qualidade, dentro da cota.',
    responsibilities: 'Triar solicitações, decompor demandas, delegar e acompanhar tarefas.',
    instructions: 'Sempre pedir aprovação humana antes de gates de release.',
    restrictions: 'Não executar deploys nem alterar políticas de roteamento.',
    bestPractices: 'Resumir decisões no chat; registrar motivos em auditoria.',
    stacks: [],
    defaultEffort: 'high',
    preferredAccountId: accountOpenAI.id,
    fallbackModelIds: [modelClaude.id],
    team: null,
    actorCritic: 'actor',
    risk: 'medium',
    version: 2,
    history: [
      {
        version: 2,
        changedAt: tick(),
        changedFields: ['fallbackModelIds', 'defaultEffort'],
        summary: 'Campos alterados: fallbackModelIds, defaultEffort.',
      },
    ],
  };
  const defBackend: AgentDefinition = {
    id: id(),
    key: 'backend-engineer',
    name: 'Engenheiro Backend',
    role: 'specialist',
    specialty: 'APIs e serviços .NET',
    description: 'Implementa e corrige código de servidor.',
    defaultModelId: modelClaude.id,
    skillIds: [skillTestes.id],
    toolIds: [toolShell.id, toolFs.id, toolGit.id],
    state: 'enabled',
    persona: 'Engenheiro criterioso, focado em contratos e testes.',
    mission: 'Implementar APIs robustas e bem testadas.',
    responsibilities: 'Endpoints, regras de negócio e migrações.',
    instructions: 'Seguir os ADRs do repositório; cobrir com testes de integração.',
    restrictions: 'Não alterar o frontend nem o design system.',
    bestPractices: 'Commits pequenos com mensagens descritivas.',
    stacks: ['.NET', 'ASP.NET Core', 'PostgreSQL'],
    defaultEffort: 'high',
    preferredAccountId: accountAnthropic.id,
    fallbackModelIds: [modelGpt4o.id],
    team: 'Engenharia',
    actorCritic: 'actor',
    risk: 'medium',
    version: 1,
    history: [],
  };
  const defFrontend: AgentDefinition = {
    id: id(),
    key: 'frontend-engineer',
    name: 'Engenheiro Frontend',
    role: 'specialist',
    specialty: 'React e design system',
    description: 'Implementa telas e componentes de UI.',
    defaultModelId: modelClaude.id,
    skillIds: [skillTestes.id, skillPrototipo.id],
    toolIds: [toolShell.id, toolFs.id, toolGit.id],
    state: 'enabled',
    persona: 'Engenheiro de UI detalhista e pragmático.',
    mission: 'Entregar telas acessíveis e consistentes com o design system.',
    responsibilities: 'Componentes, páginas, testes e i18n do frontend.',
    instructions: 'Usar apenas componentes do design system e tokens de tema.',
    restrictions: 'Não adicionar dependências sem aprovação.',
    bestPractices: 'Componentes pequenos; lógica em hooks e funções puras.',
    stacks: ['React', 'TypeScript', 'Tailwind'],
    defaultEffort: 'medium',
    preferredAccountId: accountAnthropic.id,
    fallbackModelIds: [modelGpt4o.id],
    team: 'Engenharia',
    actorCritic: 'actor',
    risk: 'low',
    version: 1,
    history: [],
  };
  const defRevisor: AgentDefinition = {
    id: id(),
    key: 'reviewer',
    name: 'Revisor',
    role: 'specialist',
    specialty: 'Revisão de código',
    description: 'Revisa diffs e aprova ou aponta correções.',
    defaultModelId: modelGpt4oMini.id,
    skillIds: [skillRevisao.id],
    toolIds: [toolFs.id, toolGit.id, toolGithubPr.id],
    state: 'enabled',
    persona: 'Revisor rigoroso, porém objetivo nos apontamentos.',
    mission: 'Garantir qualidade e coerência do código entregue.',
    responsibilities: 'Revisar diffs com checklist de qualidade e segurança.',
    instructions: 'Apontar problemas com referência ao arquivo e à linha.',
    restrictions: 'Não editar código — apenas revisar.',
    bestPractices: 'Priorizar correções críticas; elogiar boas soluções.',
    stacks: [],
    defaultEffort: 'medium',
    preferredAccountId: accountOpenAI.id,
    fallbackModelIds: [modelClaude.id],
    team: 'Qualidade',
    actorCritic: 'critic',
    risk: 'low',
    version: 1,
    history: [],
  };
  const defTestador: AgentDefinition = {
    id: id(),
    key: 'tester',
    name: 'Testador',
    role: 'specialist',
    specialty: 'Qualidade e testes E2E',
    description: 'Executa suítes de teste e reporta evidências.',
    defaultModelId: modelGpt4oMini.id,
    skillIds: [skillTestes.id],
    toolIds: [toolShell.id, toolGit.id],
    state: 'enabled',
    persona: 'Testador metódico, orientado a evidências.',
    mission: 'Manter a suíte verde e reportar falhas com reprodução.',
    responsibilities: 'Executar testes unitários, de componente e E2E.',
    instructions: 'Anexar logs e screenshots às evidências de falha.',
    restrictions: 'Não corrigir código — apenas reportar.',
    bestPractices: 'Reproduzir a falha antes de abrir o report.',
    stacks: ['Playwright', 'Vitest'],
    defaultEffort: 'medium',
    preferredAccountId: accountOpenAI.id,
    fallbackModelIds: [modelClaude.id],
    team: 'Qualidade',
    actorCritic: 'actor',
    risk: 'low',
    version: 1,
    history: [],
  };
  const defDesigner: AgentDefinition = {
    id: id(),
    key: 'prototype-designer',
    name: 'Designer de Protótipos',
    role: 'specialist',
    specialty: 'Protótipos de UI',
    description: 'Gera protótipos navegáveis a partir de documentos.',
    defaultModelId: modelGpt4o.id,
    skillIds: [skillPrototipo.id],
    toolIds: [toolFs.id, toolFigma.id],
    state: 'disabled',
    persona: 'Designer explorador, com atenção a fluxos e hierarquia visual.',
    mission: 'Materializar ideias em protótipos navegáveis rapidamente.',
    responsibilities: 'Gerar protótipos a partir de documentos aprovados.',
    instructions: 'Respeitar o design system e as referências visuais.',
    restrictions: 'Não publicar protótipos sem aprovação humana.',
    bestPractices: 'Começar pelo fluxo principal; iterar em baixa fidelidade.',
    stacks: ['HTML', 'CSS'],
    defaultEffort: 'medium',
    preferredAccountId: accountOpenAI.id,
    fallbackModelIds: [modelGpt4oMini.id],
    team: 'Design',
    actorCritic: 'actor',
    risk: 'medium',
    version: 1,
    history: [],
  };
  // Definição nunca utilizada (sem instâncias) — exercita a exclusão
  // permitida no CRUD de definições (FR-5); as demais só podem ser arquivadas.
  const defSeguranca: AgentDefinition = {
    id: id(),
    key: 'security-analyst',
    name: 'Analista de Segurança',
    role: 'specialist',
    specialty: 'Análise de vulnerabilidades',
    description: 'Analisa o código em busca de vulnerabilidades e segredos expostos.',
    defaultModelId: modelClaude.id,
    skillIds: [skillRevisao.id],
    toolIds: [toolFs.id, toolGit.id],
    state: 'enabled',
    persona: 'Analista cético, assume que todo input é hostil.',
    mission: 'Reduzir a superfície de ataque do projeto.',
    responsibilities: 'Revisar dependências, segredos e padrões inseguros.',
    instructions: 'Classificar achados por severidade (OWASP).',
    restrictions: 'Não executar exploits — apenas análise estática.',
    bestPractices: 'Reportar com referência ao CWE correspondente.',
    stacks: ['Semgrep'],
    defaultEffort: 'high',
    preferredAccountId: accountAnthropic.id,
    fallbackModelIds: [modelGpt4o.id],
    team: 'Qualidade',
    actorCritic: 'critic',
    risk: 'low',
    version: 1,
    history: [],
  };

  const metrics = () => ({
    tasksCompleted: int(random, 0, 24),
    tokensInput: int(random, 10_000, 900_000),
    tokensOutput: int(random, 5_000, 300_000),
    costUsd: Math.round(random() * 1200) / 100,
    uptimeMs: int(random, 60_000, 7_200_000),
  });

  const chefePoseidon: Agent = {
    id: id(),
    definitionId: defChefe.id,
    projectId: null,
    name: 'Chefe — Poseidon Frontend',
    state: 'waiting',
    currentTaskId: null,
    modelId: null,
    lease: { fencingToken: 3, expiresAt: tick() },
    metrics: metrics(),
    lastHeartbeatAt: tick(),
  };
  const chefePagamentos: Agent = {
    id: id(),
    definitionId: defChefe.id,
    projectId: null,
    name: 'Chefe — API de Pagamentos',
    state: 'idle',
    currentTaskId: null,
    modelId: null,
    lease: { fencingToken: 1, expiresAt: tick() },
    metrics: metrics(),
    lastHeartbeatAt: tick(),
  };
  const agenteBackend: Agent = {
    id: id(),
    definitionId: defBackend.id,
    projectId: null,
    name: 'Iara (Backend)',
    state: 'working',
    currentTaskId: null, // preenchido após criar as tarefas
    modelId: null,
    lease: null,
    metrics: metrics(),
    lastHeartbeatAt: tick(),
  };
  const agenteFrontend: Agent = {
    id: id(),
    definitionId: defFrontend.id,
    projectId: null,
    name: 'Otávio (Frontend)',
    state: 'idle',
    currentTaskId: null,
    modelId: null,
    lease: null,
    metrics: metrics(),
    lastHeartbeatAt: tick(),
  };
  const agenteRevisor: Agent = {
    id: id(),
    definitionId: defRevisor.id,
    projectId: null,
    name: 'Rui (Revisor)',
    state: 'waiting',
    currentTaskId: null,
    modelId: null,
    lease: null,
    metrics: metrics(),
    lastHeartbeatAt: tick(),
  };
  const agenteTestador: Agent = {
    id: id(),
    definitionId: defTestador.id,
    projectId: null,
    name: 'Lia (Testes)',
    state: 'error',
    currentTaskId: null,
    modelId: null,
    lease: null,
    metrics: metrics(),
    lastHeartbeatAt: tick(),
  };
  const agenteDesigner: Agent = {
    id: id(),
    definitionId: defDesigner.id,
    projectId: null,
    name: 'Nina (Protótipos)',
    state: 'outOfQuota',
    currentTaskId: null,
    modelId: null,
    lease: null,
    metrics: metrics(),
    lastHeartbeatAt: tick(),
  };

  /* ---- projetos ---- */

  const projetoPoseidon: Project = {
    id: id(),
    organizationId: orgPoseidon.id,
    name: 'Poseidon Frontend',
    key: 'POSEIDON',
    description: 'Console web do Harness Poseidon (React + Vite).',
    state: 'active',
    criticality: 'high',
    repositoryUrl: 'https://github.com/poseidon-labs/harness-poseidon',
    repositoryProvider: 'github',
    defaultBranch: 'develop',
    technologies: ['React', 'TypeScript', 'Vite', 'Tailwind'],
    brand: { logoUrl: null, primaryColor: null, secondaryColor: null, typography: null },
    memberProfileIds: [profile.id, profileAna.id],
    configVersion: 3,
    configHistory: [
      {
        version: 2,
        changedAt: tick(),
        changedFields: ['technologies'],
        summary: 'Campos alterados: technologies.',
      },
      {
        version: 3,
        changedAt: tick(),
        changedFields: ['defaultBranch'],
        summary: 'Campos alterados: defaultBranch.',
      },
    ],
    chiefAgentId: chefePoseidon.id,
    operationMode: 'manual',
    prototyping: { mode: 'autonomousGeneration', waiver: null },
    createdAt: tick(),
    lastActivityAt: tick(),
  };
  const projetoPagamentos: Project = {
    id: id(),
    organizationId: orgPessoal.id,
    name: 'API de Pagamentos',
    key: 'PAG',
    description: 'Serviço de cobranças e conciliação.',
    state: 'paused',
    criticality: 'critical',
    repositoryUrl: null,
    repositoryProvider: 'local',
    defaultBranch: 'main',
    technologies: ['.NET', 'PostgreSQL'],
    brand: { logoUrl: null, primaryColor: '#0EA5E9', secondaryColor: null, typography: null },
    memberProfileIds: [profile.id],
    configVersion: 1,
    configHistory: [],
    chiefAgentId: chefePagamentos.id,
    operationMode: 'semiautonomous',
    prototyping: {
      mode: 'notApplicable',
      waiver: {
        reason: 'Serviço de backend sem interface visual — prototipação dispensada.',
        grantedAt: tick(),
      },
    },
    createdAt: tick(),
    lastActivityAt: tick(),
  };
  chefePoseidon.projectId = projetoPoseidon.id;
  chefePagamentos.projectId = projetoPagamentos.id;
  agenteBackend.projectId = projetoPoseidon.id;
  agenteFrontend.projectId = projetoPoseidon.id;
  agenteRevisor.projectId = projetoPoseidon.id;
  agenteTestador.projectId = projetoPoseidon.id;
  agenteDesigner.projectId = projetoPoseidon.id;

  /* ---- solicitações (humanos) e demandas (chefe) ---- */

  const sol1: Solicitation = {
    id: id(),
    projectId: projetoPoseidon.id,
    authorProfileId: profile.id,
    kind: 'request',
    title: 'Exportar quadro em CSV',
    body: 'Preciso exportar as tarefas do quadro em CSV para a reunião de status semanal.',
    state: 'converted',
    supersedesId: null,
    createdAt: tick(),
  };
  const sol2: Solicitation = {
    id: id(),
    projectId: projetoPoseidon.id,
    authorProfileId: profile.id,
    kind: 'intervention',
    title: 'Priorizar correção do login',
    body: 'O login social está falhando em produção. Interromper o que for preciso e priorizar.',
    state: 'inAnalysis',
    supersedesId: null,
    createdAt: tick(),
  };
  const sol3: Solicitation = {
    id: id(),
    projectId: projetoPoseidon.id,
    authorProfileId: profile.id,
    kind: 'request',
    title: 'Relatório de custos por agente',
    body: 'Quero um relatório de custos e tokens por agente no cockpit.',
    state: 'open',
    supersedesId: null,
    createdAt: tick(),
  };
  const sol4: Solicitation = {
    id: id(),
    projectId: projetoPoseidon.id,
    authorProfileId: profile.id,
    kind: 'request',
    title: 'Exportar quadro em CSV (correção)',
    body: 'Corrigindo a solicitação anterior: o CSV precisa incluir a coluna de responsável.',
    state: 'answered',
    supersedesId: sol1.id,
    createdAt: tick(),
  };

  const demandaCsv: Demand = {
    id: id(),
    projectId: projetoPoseidon.id,
    solicitationId: sol1.id,
    title: 'Exportação CSV do quadro',
    description: 'Endpoint e botão de exportação com colunas de estado, responsável e prioridade.',
    state: 'inProgress',
    priority: 'medium',
    createdAt: tick(),
  };
  const demandaLogin: Demand = {
    id: id(),
    projectId: projetoPoseidon.id,
    solicitationId: sol2.id,
    title: 'Correção do login social',
    description: 'Investigar e corrigir a falha no callback OAuth em produção.',
    state: 'open',
    priority: 'critical',
    createdAt: tick(),
  };
  const demandaCustos: Demand = {
    id: id(),
    projectId: projetoPoseidon.id,
    solicitationId: null,
    title: 'Painel de custos por agente',
    description: 'Agregar custo e tokens por agente no cockpit.',
    state: 'completed',
    priority: 'low',
    createdAt: tick(),
  };
  const demandaConciliacao: Demand = {
    id: id(),
    projectId: projetoPagamentos.id,
    solicitationId: null,
    title: 'Conciliação diária de cobranças',
    description: 'Rotina de conciliação com o gateway às 6h.',
    state: 'inProgress',
    priority: 'high',
    createdAt: tick(),
  };

  /* ---- tarefas (criadas pelo chefe) ---- */

  const titulosPorColuna: Record<TaskState, string[]> = {
    backlog: [
      'Mapear endpoints de billing',
      'Definir tokens de espaçamento do DS',
      'Pesquisar libs de diff visual',
      'Rascunhar fluxo de onboarding',
      'Criar seeds de demonstração',
    ],
    ready: [
      'Implementar tela de notificações',
      'Adicionar filtro por responsável no quadro',
      'Criar hook de paginação por cursor',
      'Extrair componente de badge de status',
      'Configurar storybook dos cards de tarefa',
    ],
    development: [
      'Tela de detalhe da tarefa',
      'Exportação CSV do quadro',
      'Integrar React Query ao board',
      'Cliente realtime com re-sync',
    ],
    review: [
      'Correção do callback OAuth',
      'Refatorar AppShell para mobile',
      'Validação de formulários com zod',
      'Cache de listagens do cockpit',
    ],
    corrections: [
      'Ajustar contraste no tema escuro',
      'Corrigir scroll do drawer de detalhes',
      'Tratar erro 401 no interceptor',
    ],
    testsGates: [
      'Suíte E2E do fluxo de aprovação',
      'Testes de contrato do MockApiClient',
      'Validar acessibilidade do chat',
      'Gate de qualidade da sprint 12',
    ],
    blocked: [
      'Deploy em staging (sem credencial)',
      'Migração do banco de mensagens',
      'Integração Figma (plugin com erro)',
    ],
    done: [
      'Setup do Vite com TS strict',
      'Design tokens dark/light',
      'Rotas lazy das 21 features',
      'i18n pt-BR no shell',
      'Paginação por cursor no mock',
      'Schemas zod dos recursos',
    ],
  };

  const progressoPorColuna = (state: TaskState) => {
    switch (state) {
      case 'backlog':
      case 'ready':
        return { executed: 0, validated: 0, approved: 0 };
      case 'development':
        return { executed: int(random, 25, 70), validated: 0, approved: 0 };
      case 'review':
        return { executed: int(random, 70, 95), validated: int(random, 0, 30), approved: 0 };
      case 'corrections':
        return { executed: int(random, 40, 80), validated: int(random, 10, 40), approved: 0 };
      case 'testsGates':
        return { executed: 100, validated: int(random, 50, 90), approved: 0 };
      case 'blocked':
        return { executed: int(random, 20, 60), validated: 0, approved: 0 };
      case 'done':
        return { executed: 100, validated: 100, approved: 100 };
    }
  };

  const responsaveis = [agenteBackend, agenteFrontend, agenteRevisor, agenteTestador];
  const motivosBloqueio: Record<string, string> = {
    'Deploy em staging (sem credencial)': 'Credencial de deploy ainda não provisionada.',
    'Migração do banco de mensagens': 'Aguardando janela de manutenção aprovada.',
    'Integração Figma (plugin com erro)': 'Plugin figma-bridge em estado de erro.',
  };

  const tasks: Task[] = [];
  const taskInstructions: TaskInstruction[] = [];
  const attempts: Attempt[] = [];
  const attemptEvents: AttemptEvent[] = [];

  const makeTasks = (project: Project, demandId: Ulid | null) => {
    for (const [state, titulos] of Object.entries(titulosPorColuna) as [TaskState, string[]][]) {
      for (const titulo of titulos) {
        const assignee =
          state === 'backlog' || state === 'ready'
            ? null
            : pick(random, responsaveis).id;
        const task: Task = {
          id: id(),
          projectId: project.id,
          demandId,
          title: titulo,
          state,
          priority: pick(random, ['low', 'medium', 'high', 'critical'] as const),
          assigneeAgentId: assignee,
          blockedReason: state === 'blocked' ? (motivosBloqueio[titulo] ?? 'Bloqueio externo.') : null,
          instructionVersion: 1,
          progress: progressoPorColuna(state),
          createdAt: tick(),
          updatedAt: tick(),
          dueAt: null,
          // Exemplo de metaestado: uma tarefa concluída já arquivada.
          archivedAt: titulo === 'Setup do Vite com TS strict' ? tick() : null,
        };
        tasks.push(task);
        taskInstructions.push({
          id: id(),
          taskId: task.id,
          version: 1,
          body: `Instrução inicial do chefe para "${titulo}": seguir o padrão do repositório, cobrir com testes e registrar evidências na tentativa.`,
          authorKind: 'chief',
          authorId: project.chiefAgentId,
          createdAt: tick(),
        });
      }
    }
  };
  makeTasks(projetoPoseidon, demandaCsv.id);

  // Projeto 2: conjunto menor e focado.
  const tarefasPagamentos: Array<[string, TaskState]> = [
    ['Modelar entidade de cobrança', 'done'],
    ['Endpoint de conciliação', 'development'],
    ['Webhook do gateway', 'backlog'],
    ['Testes de idempotência', 'ready'],
    ['Alertas de falha de cobrança', 'backlog'],
    ['Runbook de incidentes', 'done'],
  ];
  for (const [titulo, state] of tarefasPagamentos) {
    const task: Task = {
      id: id(),
      projectId: projetoPagamentos.id,
      demandId: demandaConciliacao.id,
      title: titulo,
      state,
      priority: pick(random, ['medium', 'high'] as const),
      assigneeAgentId: state === 'development' ? agenteBackend.id : null,
      blockedReason: null,
      instructionVersion: 1,
      progress: progressoPorColuna(state),
      createdAt: tick(),
      updatedAt: tick(),
      dueAt: null,
      archivedAt: null,
    };
    tasks.push(task);
    taskInstructions.push({
      id: id(),
      taskId: task.id,
      version: 1,
      body: `Instrução inicial do chefe para "${titulo}" no projeto ${projetoPagamentos.key}.`,
      authorKind: 'chief',
      authorId: chefePagamentos.id,
      createdAt: tick(),
    });
  }

  // Algumas tarefas têm instrução v2 (correção do usuário/chefe).
  for (const task of tasks.filter((t) => t.state === 'corrections' || t.state === 'review')) {
    task.instructionVersion = 2;
    taskInstructions.push({
      id: id(),
      taskId: task.id,
      version: 2,
      body: `Correção da instrução de "${task.title}": incluir tratamento de erro e atualizar o HANDOFF.`,
      authorKind: 'user',
      authorId: profile.id,
      createdAt: tick(),
    });
  }

  // Attempts com evidências para tarefas em andamento/concluídas.
  const tarefasComAttempt = tasks.filter(
    (t) => !['backlog', 'ready'].includes(t.state),
  );
  let attemptRunning: Attempt | null = null;
  for (const task of tarefasComAttempt) {
    const quantidade = task.state === 'done' ? int(random, 1, 2) : 1;
    for (let n = 1; n <= quantidade; n += 1) {
      const isRunning =
        task.title === 'Cliente realtime com re-sync' && n === 1 && attemptRunning === null;
      const failed = task.state === 'corrections' && n === 1;
      const startedAt = tick();
      const attempt: Attempt = {
        id: id(),
        taskId: task.id,
        number: n,
        state: isRunning ? 'running' : failed ? 'failed' : 'completed',
        agentId: task.assigneeAgentId ?? agenteBackend.id,
        startedAt,
        finishedAt: isRunning ? null : tick(),
        durationMs: isRunning ? null : int(random, 45_000, 1_800_000),
        costUsd: Math.round(random() * 250) / 100,
        tokensInput: int(random, 2_000, 120_000),
        tokensOutput: int(random, 800, 40_000),
        commitRefs: task.state === 'done' ? [`feat:${task.title.toLowerCase().replaceAll(' ', '-')}`] : [],
        summary: isRunning ? null : `Resumo da tentativa ${n} de "${task.title}".`,
        failureReason: failed ? 'Testes de regressão falharam após o ajuste.' : null,
      };
      attempts.push(attempt);
      if (isRunning) {
        attemptRunning = attempt;
        agenteBackend.currentTaskId = task.id;
      }
      for (let e = 0; e < int(random, 1, 3); e += 1) {
        attemptEvents.push({
          id: id(),
          attemptId: attempt.id,
          kind: pick(random, ['log', 'toolCall', 'note', 'diff'] as const),
          content: pick(random, [
            'Analisando arquivos afetados…',
            'shell: npm run test -- --run',
            'git diff --stat (3 arquivos alterados)',
            'Decisão: reutilizar hook existente de paginação.',
          ]),
          occurredAt: tick(),
        });
      }
    }
  }

  /* ---- conversas e mensagens ---- */

  const conversaSprint: Conversation = {
    id: id(),
    projectId: projetoPoseidon.id,
    title: 'Planejamento da sprint 12',
    state: 'active',
    createdByProfileId: profile.id,
    createdAt: tick(),
    lastMessageAt: null,
  };
  const conversaGate: Conversation = {
    id: id(),
    projectId: projetoPoseidon.id,
    title: 'Dúvidas sobre o gate de release',
    state: 'active',
    createdByProfileId: profile.id,
    createdAt: tick(),
    lastMessageAt: null,
  };
  const conversaPagamentos: Conversation = {
    id: id(),
    projectId: projetoPagamentos.id,
    title: 'Onboarding do projeto de pagamentos',
    state: 'archived',
    createdByProfileId: profile.id,
    createdAt: tick(),
    lastMessageAt: null,
  };

  const messages: Message[] = [];
  const addMessage = (
    conversation: Conversation,
    role: Message['authorRole'],
    content: string,
    agentId: Ulid | null = null,
  ) => {
    const createdAt = tick();
    messages.push({
      id: id(),
      conversationId: conversation.id,
      authorRole: role,
      authorProfileId: role === 'user' ? profile.id : null,
      authorAgentId: agentId,
      content,
      tokenCount: role === 'user' ? null : int(random, 40, 600),
      createdAt,
    });
    conversation.lastMessageAt = createdAt;
  };

  addMessage(conversaSprint, 'user', 'Chefe, preciso exportar o quadro em CSV até sexta.');
  addMessage(
    conversaSprint,
    'chief',
    'Entendi. Vou criar uma demanda de exportação CSV e delegar à Iara (Backend) e ao Otávio (Frontend).',
    chefePoseidon.id,
  );
  addMessage(conversaSprint, 'user', 'Inclui a coluna de responsável, por favor.');
  addMessage(
    conversaSprint,
    'chief',
    'Registrado como correção da solicitação — criei a versão 2 com a coluna de responsável.',
    chefePoseidon.id,
  );
  addMessage(
    conversaSprint,
    'agent',
    'Demanda aceita. Iniciando a tarefa "Exportação CSV do quadro".',
    agenteBackend.id,
  );
  addMessage(conversaSprint, 'user', 'Ótimo. Como está o gate de release?');
  addMessage(
    conversaSprint,
    'chief',
    'O gate de qualidade está pendente de aprovação humana; o modo atual é manual.',
    chefePoseidon.id,
  );
  addMessage(conversaGate, 'user', 'O que falta para aprovar o gate de release?');
  addMessage(
    conversaGate,
    'chief',
    'Falta a suíte E2E do fluxo de aprovação passar e a Lia voltar do estado de erro.',
    chefePoseidon.id,
  );
  addMessage(conversaGate, 'user', 'Certo, me avisa quando destravar.');
  addMessage(
    conversaPagamentos,
    'chief',
    'Projeto de pagamentos configurado em modo semiautônomo, pausando no gate de release.',
    chefePagamentos.id,
  );
  addMessage(conversaPagamentos, 'user', 'Perfeito, obrigado.');

  /* ---- documentos (+ versões, classificações, waiver) ---- */

  const documents: Document[] = [];
  const documentVersions: DocumentVersion[] = [];
  const addDocument = (
    input: Pick<Document, 'title' | 'kind' | 'state'> &
      Partial<Pick<Document, 'classifications' | 'inconsistent' | 'waiver' | 'phaseName'>>,
    corpos: string[],
  ) => {
    const doc: Document = {
      id: id(),
      projectId: projetoPoseidon.id,
      title: input.title,
      kind: input.kind,
      state: input.state,
      currentVersion: corpos.length,
      classifications: input.classifications ?? [],
      phaseName: input.phaseName ?? null,
      inconsistent: input.inconsistent ?? false,
      waiver: input.waiver ?? null,
      createdAt: tick(),
      updatedAt: tick(),
    };
    documents.push(doc);
    corpos.forEach((body, index) => {
      documentVersions.push({
        id: id(),
        documentId: doc.id,
        version: index + 1,
        body,
        authorKind: index === 0 ? 'chief' : 'user',
        authorId: index === 0 ? chefePoseidon.id : profile.id,
        createdAt: tick(),
      });
    });
    return doc;
  };

  addDocument(
    { title: 'PRD do Poseidon Console', kind: 'prd', state: 'approved', classifications: ['produto', 'normativo'], phaseName: 'Planejamento' },
    ['# PRD\n\nVisão do console web do Harness Poseidon.', '# PRD v2\n\nInclui cockpit e quadro.'],
  );
  const docSpecApi = addDocument(
    { title: 'Spec da API v1', kind: 'spec', state: 'awaitingApproval', classifications: ['arquitetura'], phaseName: 'Planejamento' },
    ['# Spec API v1\n\nContratos REST e eventos realtime.'],
  );
  addDocument(
    { title: 'Guia de UX do quadro', kind: 'design', state: 'inElaboration', classifications: ['ux'], phaseName: 'Execução' },
    ['# Guia de UX\n\nColunas, drag-and-drop e estados vazios.'],
  );
  addDocument({ title: 'Runbook de deploy', kind: 'runbook', state: 'planned', classifications: ['ops'], phaseName: 'Publicação' }, [
    '# Runbook\n\nPassos de deploy em staging e produção.',
  ]);
  addDocument(
    { title: 'Nota de arquitetura realtime', kind: 'note', state: 'awaitingApproval', classifications: ['arquitetura', 'realtime'], phaseName: 'Validação' },
    ['# Realtime\n\nHub único /hubs/events com snapshot+delta.'],
  );
  addDocument({ title: 'Spec do protótipo v0', kind: 'spec', state: 'outdated', classifications: ['ux'] }, [
    '# Protótipo v0\n\nSubstituída pela v1.',
  ]);
  addDocument({ title: 'ADR 001 — SPA simples', kind: 'note', state: 'superseded', classifications: ['arquitetura'], phaseName: 'Planejamento' }, [
    '# ADR 001\n\nDecisão antiga, substituída pelo ADR 007.',
  ]);
  addDocument({ title: 'ADR 002 — SSR', kind: 'note', state: 'notApplicable', classifications: ['arquitetura'] }, [
    '# ADR 002\n\nSSR não se aplica ao MVP.',
  ]);
  const docSpecPagamentos = addDocument(
    {
      title: 'Spec de pagamentos',
      kind: 'spec',
      state: 'inElaboration',
      classifications: ['arquitetura', 'pagamentos'],
      phaseName: 'Execução',
      inconsistent: true,
      waiver: {
        reason: 'Divergência conhecida com o gateway legado; waiver até a migração.',
        approvedByProfileId: profile.id,
        grantedAt: tick(),
        expiresAt: '2026-09-30T00:00:00Z',
      },
    },
    ['# Spec de pagamentos\n\nConciliação e webhooks.'],
  );
  docSpecPagamentos.projectId = projetoPagamentos.id;

  /* ---- protótipos e referências visuais ---- */

  const prototipos: Prototype[] = [
    {
      id: id(),
      projectId: projetoPoseidon.id,
      name: 'Cockpit v1',
      description: 'Protótipo navegável do cockpit executivo.',
      state: 'published',
      url: 'https://prototype.poseidon.local/cockpit-v1',
      thumbnailUrl: null,
      sourceDocumentId: null,
      createdAt: tick(),
      updatedAt: tick(),
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      name: 'Board v2',
      description: 'Variação do quadro com swimlanes por agente.',
      state: 'ready',
      url: 'https://prototype.poseidon.local/board-v2',
      thumbnailUrl: null,
      sourceDocumentId: null,
      createdAt: tick(),
      updatedAt: tick(),
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      name: 'Onboarding v0',
      description: 'Fluxo de primeiro acesso (rascunho).',
      state: 'draft',
      url: null,
      thumbnailUrl: null,
      sourceDocumentId: null,
      createdAt: tick(),
      updatedAt: tick(),
    },
  ];

  const referenciasVisuais: VisualReference[] = [
    {
      id: id(),
      projectId: projetoPoseidon.id,
      prototypeId: prototipos[1].id,
      title: 'Referência de kanban denso',
      imageUrl: '/refs/kanban-denso.png',
      source: 'url',
      tags: ['quadro', 'densidade'],
      createdAt: tick(),
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      prototypeId: null,
      title: 'Paleta dark de telemetria',
      imageUrl: '/refs/paleta-dark.png',
      source: 'upload',
      tags: ['tema', 'dark'],
      createdAt: tick(),
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      prototypeId: prototipos[0].id,
      title: 'Mock gerado do cockpit',
      imageUrl: '/refs/cockpit-mock.png',
      source: 'generated',
      tags: ['cockpit'],
      createdAt: tick(),
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      prototypeId: null,
      title: 'Tela de aprovações (Figma)',
      imageUrl: '/refs/aprovacoes-figma.png',
      source: 'url',
      tags: ['aprovações', 'figma'],
      createdAt: tick(),
    },
  ];

  /* ---- workflow: template, versão, vínculos, run, fases, gates ---- */

  const template: WorkflowTemplate = {
    id: id(),
    name: 'Fluxo de Entrega Padrão',
    description: 'Planejamento → Execução → Validação → Publicação, com gates de qualidade e release.',
    currentVersionId: null,
    state: 'published',
    archivedAt: null,
    createdAt: tick(),
  };
  const versao1: WorkflowVersion = {
    id: id(),
    templateId: template.id,
    version: 1,
    phases: ['Planejamento', 'Execução', 'Validação', 'Publicação'],
    gatesByPhase: {
      Validação: ['Gate de Qualidade'],
      Publicação: ['Gate de Release'],
    },
    changelog: 'Versão inicial do fluxo de entrega.',
    state: 'published',
    publishedAt: tick(),
    archivedAt: null,
  };
  template.currentVersionId = versao1.id;
  orgPoseidon.defaultWorkflowTemplateIds = [template.id];

  // Template rascunho (FR-4): editável, ainda sem versão publicada.
  const templateRascunho: WorkflowTemplate = {
    id: id(),
    name: 'Fluxo Experimental',
    description: 'Rascunho de fluxo enxuto para experimentos — ainda não publicado.',
    currentVersionId: null,
    state: 'draft',
    archivedAt: null,
    createdAt: tick(),
  };
  const versaoRascunho: WorkflowVersion = {
    id: id(),
    templateId: templateRascunho.id,
    version: 1,
    phases: ['Descoberta', 'Entrega'],
    gatesByPhase: { Entrega: ['Revisão final'] },
    phaseConfigs: {
      Descoberta: {
        documentKinds: ['prd', 'note'],
        progressWeight: 40,
        allowedAgentDefinitionIds: [],
        objective: 'Entender o problema e delimitar o experimento.',
        acceptanceCriteria: ['Escopo do experimento registrado.'],
      },
      Entrega: {
        documentKinds: ['spec', 'runbook'],
        progressWeight: 60,
        allowedAgentDefinitionIds: [],
        dependsOn: ['Descoberta'],
        exitConditions: ['Revisão final aprovada.'],
      },
    },
    changelog: null,
    state: 'draft',
    publishedAt: null,
    archivedAt: null,
  };

  const workflowPoseidon: Workflow = {
    id: id(),
    projectId: projetoPoseidon.id,
    templateId: template.id,
    activeVersionId: versao1.id,
    operationMode: 'manual',
    semiautonomousPauseGates: [],
    riskAcceptances: [
      {
        mode: 'manual',
        acceptedByProfileId: profile.id,
        note: 'Prefiro aprovar cada gate manualmente neste projeto.',
        acceptedAt: tick(),
      },
    ],
    createdAt: tick(),
  };
  const workflowPagamentos: Workflow = {
    id: id(),
    projectId: projetoPagamentos.id,
    templateId: template.id,
    activeVersionId: versao1.id,
    operationMode: 'semiautonomous',
    semiautonomousPauseGates: ['Gate de Release'],
    riskAcceptances: [
      {
        mode: 'semiautonomous',
        acceptedByProfileId: profile.id,
        note: 'Aceito execução semiautônoma com pausa apenas no gate de release.',
        acceptedAt: tick(),
      },
    ],
    createdAt: tick(),
  };

  const run1: WorkflowRun = {
    id: id(),
    workflowId: workflowPoseidon.id,
    versionId: versao1.id,
    state: 'running',
    startedAt: tick(),
    finishedAt: null,
  };

  const fases: Phase[] = [
    { id: id(), runId: run1.id, name: 'Planejamento', order: 1, state: 'completed', startedAt: tick(), finishedAt: tick() },
    { id: id(), runId: run1.id, name: 'Execução', order: 2, state: 'completed', startedAt: tick(), finishedAt: tick() },
    { id: id(), runId: run1.id, name: 'Validação', order: 3, state: 'active', startedAt: tick(), finishedAt: null },
    { id: id(), runId: run1.id, name: 'Publicação', order: 4, state: 'pending', startedAt: null, finishedAt: null },
  ];

  const gateQualidade: Gate = {
    id: id(),
    phaseId: fases[2].id,
    runId: run1.id,
    name: 'Gate de Qualidade',
    state: 'pending',
    requiresApproval: true,
    decidedByProfileId: null,
    decidedAt: null,
    note: null,
  };
  const gateRelease: Gate = {
    id: id(),
    phaseId: fases[3].id,
    runId: run1.id,
    name: 'Gate de Release',
    state: 'pending',
    requiresApproval: true,
    decidedByProfileId: null,
    decidedAt: null,
    note: null,
  };

  /* ---- aprovações ---- */

  const approvals: Approval[] = [
    {
      id: id(),
      projectId: projetoPoseidon.id,
      gateId: gateQualidade.id,
      taskId: null,
      documentId: null,
      title: 'Aprovar Gate de Qualidade',
      description: 'Evidências da sprint 12 anexadas; testes de contrato verdes.',
      priority: 'high',
      dueAt: '2026-07-19T00:00:00Z',
      state: 'pending',
      requestedByAgentId: chefePoseidon.id,
      requestedAt: tick(),
      resolvedByProfileId: null,
      resolvedAt: null,
      resolutionNote: null,
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      gateId: null,
      taskId: null,
      documentId: docSpecApi.id,
      title: 'Aprovar Spec da API v1',
      description: 'Revisão concluída; aguardando aprovação para virar referência.',
      priority: 'medium',
      dueAt: '2026-07-25T00:00:00Z',
      state: 'pending',
      requestedByAgentId: chefePoseidon.id,
      requestedAt: tick(),
      resolvedByProfileId: null,
      resolvedAt: null,
      resolutionNote: null,
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      gateId: null,
      taskId: tasks.find((t) => t.title === 'Suíte E2E do fluxo de aprovação')?.id ?? null,
      documentId: null,
      title: 'Aprovar publicação da suíte E2E',
      description: 'Suíte completa rodando em CI; aprovar para marcar o gate.',
      priority: 'critical',
      dueAt: '2026-07-18T00:00:00Z',
      state: 'pending',
      requestedByAgentId: agenteTestador.id,
      requestedAt: tick(),
      resolvedByProfileId: null,
      resolvedAt: null,
      resolutionNote: null,
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      gateId: null,
      taskId: null,
      documentId: documents[0].id,
      title: 'Aprovar PRD do console',
      description: 'PRD revisado com o time.',
      priority: 'medium',
      dueAt: null,
      state: 'approved',
      requestedByAgentId: chefePoseidon.id,
      requestedAt: tick(),
      resolvedByProfileId: profile.id,
      resolvedAt: tick(),
      resolutionNote: 'Aprovado sem ressalvas.',
    },
    {
      id: id(),
      projectId: projetoPoseidon.id,
      gateId: null,
      taskId: null,
      documentId: null,
      title: 'Aprovar aumento de budget',
      description: 'Pedido de aumento do limite mensal global.',
      priority: 'low',
      dueAt: null,
      state: 'rejected',
      requestedByAgentId: chefePoseidon.id,
      requestedAt: tick(),
      resolvedByProfileId: profile.id,
      resolvedAt: tick(),
      resolutionNote: 'Reprovado: revisar o consumo do Claude antes de aumentar.',
    },
  ];

  /* ---- notificações ---- */

  const addNotification = (
    input: Pick<Notification, 'severity' | 'category' | 'title' | 'body' | 'status'> &
      Partial<Pick<Notification, 'groupKey' | 'dedupeCount' | 'link'>>,
  ): Notification => ({
    id: id(),
    profileId: profile.id,
    severity: input.severity,
    category: input.category,
    title: input.title,
    body: input.body,
    groupKey: input.groupKey ?? null,
    dedupeCount: input.dedupeCount ?? 1,
    status: input.status,
    link: input.link ?? null,
    createdAt: tick(),
    readAt: input.status === 'unread' ? null : tick(),
  });

  const notifications: Notification[] = [
    addNotification({ severity: 'warning', category: 'approval', title: 'Aprovação pendente', body: 'Gate de Qualidade aguardando sua decisão.', status: 'unread', link: '/approvals' }),
    addNotification({ severity: 'error', category: 'task', title: 'Tentativa falhou', body: 'Ajustar contraste no tema escuro — testes de regressão falharam.', status: 'unread', link: '/board' }),
    addNotification({ severity: 'warning', category: 'quota', title: 'Cota em 80%', body: 'Conta secundária (Anthropic) atingiu 80% da cota.', status: 'unread', groupKey: 'quota:conta-secundaria', dedupeCount: 3 }),
    addNotification({ severity: 'error', category: 'system', title: 'Agente em erro', body: 'Lia (Testes) entrou em estado de erro durante a suíte E2E.', status: 'unread', link: '/agents' }),
    addNotification({ severity: 'info', category: 'license', title: 'Licença ativa', body: 'Licença Pro válida até 01/07/2027.', status: 'read' }),
    addNotification({ severity: 'info', category: 'chat', title: 'Turno concluído', body: 'Chefe respondeu em "Planejamento da sprint 12".', status: 'read', link: '/chat' }),
    addNotification({ severity: 'info', category: 'workflow', title: 'Versão publicada', body: 'Fluxo de Entrega Padrão v1 publicado.', status: 'muted' }),
    addNotification({ severity: 'critical', category: 'task', title: 'Login social quebrado', body: 'Intervenção registrada: priorizar correção do login.', status: 'read', link: '/board' }),
    addNotification({ severity: 'info', category: 'workflow', title: 'Gate aprovado', body: 'PRD do console aprovado.', status: 'read' }),
    addNotification({ severity: 'warning', category: 'quota', title: 'Budget do projeto em 42%', body: 'Poseidon Frontend consumiu US$ 41,70 de US$ 100.', status: 'muted', groupKey: 'budget:poseidon', dedupeCount: 2 }),
  ];

  /* ---- auditoria ---- */

  const auditEvents: AuditEvent[] = [
    { id: id(), actorKind: 'user', actorId: profile.id, action: 'workflow.operationModeChanged', targetType: 'workflow', targetId: workflowPoseidon.id, detail: 'Modo alterado para manual com aceite de risco.', occurredAt: tick() },
    { id: id(), actorKind: 'user', actorId: profile.id, action: 'approval.resolved', targetType: 'approval', targetId: approvals[3].id, detail: 'PRD do console aprovado.', occurredAt: tick() },
    { id: id(), actorKind: 'user', actorId: profile.id, action: 'approval.resolved', targetType: 'approval', targetId: approvals[4].id, detail: 'Aumento de budget reprovado com observação.', occurredAt: tick() },
    { id: id(), actorKind: 'chief', actorId: chefePoseidon.id, action: 'task.created', targetType: 'task', targetId: tasks[10].id, detail: 'Tarefa criada a partir da demanda de exportação CSV.', occurredAt: tick() },
    { id: id(), actorKind: 'agent', actorId: agenteTestador.id, action: 'agent.error', targetType: 'agent', targetId: agenteTestador.id, detail: 'Falha na suíte E2E: timeout no fluxo de aprovação.', occurredAt: tick() },
    { id: id(), actorKind: 'system', actorId: null, action: 'license.validated', targetType: 'license', targetId: null, detail: 'Licença Pro validada com sucesso.', occurredAt: tick() },
    { id: id(), actorKind: 'user', actorId: profile.id, action: 'solicitation.created', targetType: 'solicitation', targetId: sol2.id, detail: 'Intervenção: priorizar correção do login.', occurredAt: tick() },
    { id: id(), actorKind: 'chief', actorId: chefePoseidon.id, action: 'demand.created', targetType: 'demand', targetId: demandaCsv.id, detail: 'Demanda criada a partir da solicitação de exportação.', occurredAt: tick() },
  ];

  /* ---- run-targets, settings, licença, entitlements ---- */

  const runTargets: RunTarget[] = [
    { id: id(), projectId: projetoPoseidon.id, name: 'Frontend Vite (dev)', kind: 'http', url: 'http://localhost:5173', port: 5173, state: 'running', detectedAt: tick(), lastCheckAt: tick() },
    { id: id(), projectId: projetoPoseidon.id, name: 'Backend API (.NET)', kind: 'http', url: 'http://localhost:5001', port: 5001, state: 'stopped', detectedAt: tick(), lastCheckAt: tick() },
    { id: id(), projectId: projetoPagamentos.id, name: 'Worker de conciliação', kind: 'process', url: null, port: null, state: 'unknown', detectedAt: tick(), lastCheckAt: null },
  ];

  const settings: Settings = {
    id: id(),
    profileId: profile.id,
    theme: 'dark',
    language: 'pt-BR',
    notificationsEnabled: true,
    mutedCategories: [],
    workingDirectory: '~/poseidon',
    unsafeModeAcceptedAt: tick(),
    updatedAt: tick(),
  };
  const settingsAna: Settings = {
    id: id(),
    profileId: profileAna.id,
    theme: 'system',
    language: 'pt-BR',
    notificationsEnabled: true,
    mutedCategories: [],
    workingDirectory: null,
    unsafeModeAcceptedAt: null,
    updatedAt: tick(),
  };

  const license: License = {
    id: id(),
    state: 'active',
    plan: 'Pro',
    deviceId: 'poseidon-mac-mateus',
    deviceName: 'MacBook Pro do Mateus',
    expiresAt: '2027-07-01T00:00:00Z',
    gracePeriodEndsAt: '2027-07-15T00:00:00Z',
    offlineMode: false,
    lastValidatedAt: tick(),
  };

  const entitlements: Entitlement[] = [
    { id: id(), key: 'projects.max', description: 'Projetos ativos simultâneos', included: true, limit: 10 },
    { id: id(), key: 'agents.concurrent', description: 'Agentes concorrentes por projeto', included: true, limit: 8 },
    { id: id(), key: 'offline-mode', description: 'Modo offline com fila local', included: true, limit: null },
    { id: id(), key: 'sso.oidc', description: 'Login corporativo via OIDC', included: false, limit: null },
    { id: id(), key: 'audit.retention', description: 'Retenção do log de auditoria (dias)', included: true, limit: 90 },
  ];

  /* ---- budgets e política de roteamento ---- */

  const budgets: Budget[] = [
    { id: id(), scope: 'global', scopeId: null, period: 'monthly', limitUsd: 200, spentUsd: 86.42, alertThresholdPct: 80 },
    { id: id(), scope: 'project', scopeId: projetoPoseidon.id, period: 'monthly', limitUsd: 100, spentUsd: 41.7, alertThresholdPct: 80 },
    { id: id(), scope: 'account', scopeId: accountAnthropic.id, period: 'monthly', limitUsd: 60, spentUsd: 48.1, alertThresholdPct: 75 },
  ];

  const routingPolicies: RoutingPolicy[] = [
    {
      id: id(),
      projectId: null,
      name: 'Política padrão',
      active: true,
      rules: [
        { taskKind: 'code', preferredModelId: modelClaude.id, fallbackModelIds: [modelGpt4o.id], maxCostPerAttemptUsd: 0.5 },
        { taskKind: 'review', preferredModelId: modelGpt4oMini.id, fallbackModelIds: [modelClaude.id], maxCostPerAttemptUsd: 0.1 },
        { taskKind: null, preferredModelId: modelGpt4o.id, fallbackModelIds: [modelGpt4oMini.id, modelLlama.id], maxCostPerAttemptUsd: null },
      ],
    },
  ];

  /* ---- montagem ---- */

  const data: FixtureData['data'] = {
    profiles: [profile, profileAna],
    organizations: [orgPoseidon, orgPessoal],
    projects: [projetoPoseidon, projetoPagamentos],
    conversations: [conversaSprint, conversaGate, conversaPagamentos],
    messages,
    solicitations: [sol1, sol2, sol3, sol4],
    demands: [demandaCsv, demandaLogin, demandaCustos, demandaConciliacao],
    tasks,
    'task-instructions': taskInstructions,
    attempts,
    'attempt-events': attemptEvents,
    'workflow-templates': [template, templateRascunho],
    'workflow-versions': [versao1, versaoRascunho],
    workflows: [workflowPoseidon, workflowPagamentos],
    'workflow-runs': [run1],
    phases: fases,
    gates: [gateQualidade, gateRelease],
    approvals,
    documents,
    'document-versions': documentVersions,
    prototypes: prototipos,
    'visual-references': referenciasVisuais,
    'agent-definitions': [defChefe, defBackend, defFrontend, defRevisor, defTestador, defDesigner, defSeguranca],
    agents: [chefePoseidon, chefePagamentos, agenteBackend, agenteFrontend, agenteRevisor, agenteTestador, agenteDesigner],
    skills: [skillPlanejamento, skillRevisao, skillTestes, skillDocs, skillPrototipo],
    tools: [toolShell, toolFs, toolGit, toolWebSearch, toolGithubPr, toolFigma],
    plugins: [pluginGithub, pluginFigma],
    'mcp-servers': [mcpGithub, mcpFs],
    providers: [providerOpenAI, providerAnthropic, providerOllama],
    accounts: [accountOpenAI, accountAnthropic, accountOllama],
    models: [modelGpt4o, modelGpt4oMini, modelClaude, modelLlama],
    'routing-policies': routingPolicies,
    budgets,
    notifications,
    'audit-events': auditEvents,
    'run-targets': runTargets,
    settings: [settings, settingsAna],
    licenses: [license],
    entitlements,
  };

  return {
    meta: { seed, currentProfileId: profile.id },
    data,
  };
}

/** Dataset padrão (seed fixa) — instância única para app e testes. */
export const fixtures = buildFixtures();
