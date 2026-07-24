import type {
  ArchitectureBaseline,
  ArchitecturePattern,
  BaselineComparison,
  CapabilityMap,
  Discovery,
  DiscoverySummaryList,
  DomainMap,
  IntegrationGraph,
  PortfolioReuse,
  RationalizationReport,
  System360,
  SystemCatalog,
  SystemCatalogEntry,
  SystemHeatmap,
} from './types';

/**
 * Fixture determinística do Architecture Hub (modo mock / testes). Cobre o
 * catálogo corporativo de sistemas (ARC-02), Sistema 360 (ARC-03), Discovery
 * (ARC-06), Insights/Racionalização (ARC-07), Padrões/ADRs (ARC-08) e
 * Baselines/Conformidade (ARC-10). Todas as views do Hub têm conteúdo sem
 * depender do backend. Coerente com o modelo semeado em `mock-data.ts`.
 */

interface SystemSeed {
  id: string;
  name: string;
  description: string;
  domain: string;
  capabilities: string[];
  owner: string;
  criticality: string;
  lifecycleStatus: string;
  heatSignals: string[];
  heatScore: number;
}

const SYSTEMS: SystemSeed[] = [
  {
    id: 'sys-poseidon',
    name: 'Poseidon',
    description: 'Control plane de agentes durável.',
    domain: 'Plataforma',
    capabilities: ['Orquestração', 'Governança', 'Observabilidade'],
    owner: 'Time Plataforma',
    criticality: 'high',
    lifecycleStatus: 'active',
    heatSignals: [],
    heatScore: 12,
  },
  {
    id: 'sys-payment',
    name: 'Gateway de Pagamento',
    description: 'Sistema externo de cobrança.',
    domain: 'Financeiro',
    capabilities: ['Cobrança', 'Conciliação'],
    owner: 'Fornecedor externo',
    criticality: 'high',
    lifecycleStatus: 'active',
    heatSignals: ['single-owner', 'high-cost'],
    heatScore: 58,
  },
  {
    id: 'sys-billing-legacy',
    name: 'Faturamento Legado',
    description: 'ERP de faturamento em fim de vida.',
    domain: 'Financeiro',
    capabilities: ['Cobrança', 'Emissão fiscal'],
    owner: 'Maria Silva',
    criticality: 'medium',
    lifecycleStatus: 'deprecated',
    heatSignals: ['end-of-life', 'no-owner-backup', 'stale-review'],
    heatScore: 84,
  },
  {
    id: 'sys-crm',
    name: 'CRM Comercial',
    description: 'Gestão de relacionamento e pipeline de vendas.',
    domain: 'Comercial',
    capabilities: ['Gestão de clientes', 'Pipeline'],
    owner: 'Time Comercial',
    criticality: 'medium',
    lifecycleStatus: 'active',
    heatSignals: ['stale-review'],
    heatScore: 34,
  },
  {
    id: 'sys-identity',
    name: 'Identidade e Acesso',
    description: 'IdP corporativo com SSO e MFA.',
    domain: 'Segurança',
    capabilities: ['Autenticação', 'Autorização'],
    owner: 'Time Segurança',
    criticality: 'high',
    lifecycleStatus: 'active',
    heatSignals: [],
    heatScore: 8,
  },
  {
    id: 'sys-analytics',
    name: 'Plataforma de Analytics',
    description: 'Data lake e relatórios corporativos.',
    domain: 'Dados',
    capabilities: ['Relatórios', 'BI'],
    owner: 'Time Dados',
    criticality: 'low',
    lifecycleStatus: 'active',
    heatSignals: ['high-cost'],
    heatScore: 41,
  },
];

const HEAT_DETAIL: Record<string, string> = {
  'single-owner': 'Dono único sem backup declarado.',
  'high-cost': 'Custo mensal acima da mediana do portfólio.',
  'end-of-life': 'Tecnologia em fim de vida sem plano de sucessão.',
  'no-owner-backup': 'Bus factor 1 — risco de conhecimento.',
  'stale-review': 'Última revisão de arquitetura há mais de 12 meses.',
};

function scoped(projectId: string | null, id: string): string {
  return projectId ? `${projectId}::${id}` : id;
}

function entry(projectId: string | null, s: SystemSeed): SystemCatalogEntry {
  return {
    id: scoped(projectId, s.id),
    name: s.name,
    description: s.description,
    domain: s.domain,
    capabilities: s.capabilities,
    owner: s.owner,
    criticality: s.criticality,
    lifecycleStatus: s.lifecycleStatus,
    heatSignals: s.heatSignals,
    heatScore: s.heatScore,
  };
}

export function buildSystemCatalog(projectId: string | null): SystemCatalog {
  const systems = SYSTEMS.map((s) => entry(projectId, s));
  return { total: systems.length, nextCursor: null, systems };
}

export function buildDomainMap(projectId: string | null): DomainMap {
  const byDomain = new Map<string, string[]>();
  for (const s of SYSTEMS) {
    const list = byDomain.get(s.domain) ?? [];
    list.push(scoped(projectId, s.id));
    byDomain.set(s.domain, list);
  }
  const domains = [...byDomain.entries()]
    .map(([domain, systemIds]) => ({ domain, systemCount: systemIds.length, systemIds }))
    .sort((a, b) => b.systemCount - a.systemCount || a.domain.localeCompare(b.domain));
  return { total: domains.length, domains };
}

export function buildCapabilityMap(projectId: string | null): CapabilityMap {
  const byCap = new Map<string, string[]>();
  for (const s of SYSTEMS) {
    for (const cap of s.capabilities) {
      const list = byCap.get(cap) ?? [];
      list.push(scoped(projectId, s.id));
      byCap.set(cap, list);
    }
  }
  const capabilities = [...byCap.entries()]
    .map(([capability, systemIds]) => ({
      capability,
      systemCount: systemIds.length,
      systemIds,
    }))
    .sort((a, b) => b.systemCount - a.systemCount || a.capability.localeCompare(b.capability));
  return { total: capabilities.length, capabilities };
}

interface EdgeSeed {
  source: string;
  target: string;
  kind: string;
}

const EDGES: EdgeSeed[] = [
  { source: 'sys-poseidon', target: 'sys-payment', kind: 'calls' },
  { source: 'sys-poseidon', target: 'sys-identity', kind: 'authenticates-with' },
  { source: 'sys-crm', target: 'sys-billing-legacy', kind: 'calls' },
  { source: 'sys-billing-legacy', target: 'sys-payment', kind: 'calls' },
  { source: 'sys-analytics', target: 'sys-crm', kind: 'reads' },
  { source: 'sys-analytics', target: 'sys-billing-legacy', kind: 'reads' },
];

function nameOf(id: string): string {
  return SYSTEMS.find((s) => s.id === id)?.name ?? id;
}

export function buildIntegrationGraph(projectId: string | null): IntegrationGraph {
  const edges = EDGES.map((e) => ({
    sourceId: scoped(projectId, e.source),
    sourceName: nameOf(e.source),
    targetId: scoped(projectId, e.target),
    targetName: nameOf(e.target),
    kind: e.kind,
  }));
  return { systemCount: SYSTEMS.length, edgeCount: edges.length, edges };
}

export function buildHeatmap(projectId: string | null): SystemHeatmap {
  const systems = SYSTEMS.filter((s) => s.heatSignals.length > 0)
    .map((s) => ({
      id: scoped(projectId, s.id),
      name: s.name,
      domain: s.domain,
      score: s.heatScore,
      signals: s.heatSignals.map((code) => ({ code, detail: HEAT_DETAIL[code] ?? code })),
    }))
    .sort((a, b) => b.score - a.score);
  const signalTotals: Record<string, number> = {};
  for (const s of SYSTEMS) {
    for (const code of s.heatSignals) {
      signalTotals[code] = (signalTotals[code] ?? 0) + 1;
    }
  }
  return { total: systems.length, signalTotals, systems };
}

export function buildSystem360(projectId: string | null, systemId: string): System360 | null {
  const bare = systemId.includes('::') ? systemId.split('::').pop()! : systemId;
  const s = SYSTEMS.find((x) => x.id === bare);
  if (!s) return null;
  const outgoing = EDGES.filter((e) => e.source === s.id).map((e) => ({
    sourceId: scoped(projectId, e.source),
    sourceName: nameOf(e.source),
    targetId: scoped(projectId, e.target),
    targetName: nameOf(e.target),
    kind: e.kind,
  }));
  const incoming = EDGES.filter((e) => e.target === s.id).map((e) => ({
    sourceId: scoped(projectId, e.source),
    sourceName: nameOf(e.source),
    targetId: scoped(projectId, e.target),
    targetName: nameOf(e.target),
    kind: e.kind,
  }));
  return {
    systemId: scoped(projectId, s.id),
    business: {
      name: s.name,
      description: s.description,
      domain: s.domain,
      capabilities: s.capabilities,
      criticality: s.criticality,
      owner: s.owner,
    },
    technology: {
      techStack: s.id === 'sys-poseidon' ? ['.NET', 'React', 'PostgreSQL'] : ['Java', 'Oracle'],
      lifecycleStatus: s.lifecycleStatus,
      containers: [
        {
          id: scoped(projectId, `${s.id}-api`),
          projectId,
          kind: 'container',
          name: `${s.name} API`,
          description: 'Serviço de aplicação principal.',
          properties: {},
          state: 'baseline',
          locked: false,
          version: 1,
        },
      ],
    },
    integrations: { outgoing, incoming },
    operation: {
      sla: s.criticality === 'high' ? '99.9%' : '99.0%',
      incidentCount: s.heatSignals.length,
      lastIncidentAt: s.heatSignals.length > 0 ? '2026-06-01T12:00:00Z' : null,
      backupPolicy: 'Diário incremental, retenção 30 dias.',
      drPolicy: s.criticality === 'high' ? 'Ativo-passivo multi-região.' : null,
      costMonthlyUsd: s.heatSignals.includes('high-cost') ? 4200 : 900,
    },
    data: {
      pii: s.domain === 'Comercial' || s.domain === 'Financeiro' || s.domain === 'Segurança',
      sensitive: s.domain === 'Financeiro' || s.domain === 'Segurança',
      retention: '5 anos',
      dataClasses: s.domain === 'Financeiro' ? ['fiscal', 'pagamento'] : ['operacional'],
    },
    governance: {
      documents: [
        { id: scoped(projectId, `${s.id}-adr-1`), title: `ADR: ${s.name}`, kind: 'adr', state: 'accepted' },
      ],
      adrCount: 1,
      risks: s.heatSignals.map((code) => HEAT_DETAIL[code] ?? code),
      lastReviewAt: s.heatSignals.includes('stale-review') ? '2024-01-15T00:00:00Z' : '2026-05-01T00:00:00Z',
      reviewConfidence: s.heatSignals.includes('stale-review') ? 'low' : 'high',
      busFactor: s.heatSignals.includes('no-owner-backup') ? 1 : 3,
    },
  };
}

export function buildDiscoveries(projectId: string | null): Discovery[] {
  const now = '2026-07-20T10:00:00Z';
  return [
    {
      id: scoped(projectId, 'disc-1'),
      projectId,
      systemId: scoped(projectId, 'sys-billing-legacy'),
      subjectName: 'Faturamento Legado',
      sourceKind: 'interview',
      field: 'owner',
      value: 'Maria Silva (sem backup)',
      confidence: 'high',
      evidence: 'Entrevista com o time financeiro em 2026-07-18.',
      pendingQuestions: ['Existe plano de sucessão?'],
      status: 'confirmed',
      createdAt: now,
      updatedAt: now,
    },
    {
      id: scoped(projectId, 'disc-2'),
      projectId,
      systemId: scoped(projectId, 'sys-billing-legacy'),
      subjectName: 'Faturamento Legado',
      sourceKind: 'document',
      field: 'lifecycleStatus',
      value: 'deprecated',
      confidence: 'medium',
      evidence: 'Roadmap de TI menciona descomissionamento em 2027.',
      pendingQuestions: ['Qual o sistema sucessor?', 'Há dependências externas?'],
      status: 'open',
      createdAt: now,
      updatedAt: now,
    },
    {
      id: scoped(projectId, 'disc-3'),
      projectId,
      systemId: scoped(projectId, 'sys-analytics'),
      subjectName: 'Plataforma de Analytics',
      sourceKind: 'scan',
      field: 'costMonthlyUsd',
      value: '4200',
      confidence: 'high',
      evidence: 'Relatório de billing do provedor de nuvem.',
      pendingQuestions: [],
      status: 'confirmed',
      createdAt: now,
      updatedAt: now,
    },
  ];
}

export function buildDiscoverySummary(projectId: string | null): DiscoverySummaryList {
  const discoveries = buildDiscoveries(projectId);
  const bySubject = new Map<string, Discovery[]>();
  for (const d of discoveries) {
    const key = d.subjectName;
    const list = bySubject.get(key) ?? [];
    list.push(d);
    bySubject.set(key, list);
  }
  const subjects = [...bySubject.values()].map((list) => {
    const bySource: Record<string, number> = {};
    for (const d of list) bySource[d.sourceKind] = (bySource[d.sourceKind] ?? 0) + 1;
    const open = list.filter((d) => d.status === 'open').length;
    const confirmed = list.filter((d) => d.status === 'confirmed').length;
    return {
      systemId: list[0].systemId,
      subjectName: list[0].subjectName,
      discoveryCount: list.length,
      openCount: open,
      confirmedCount: confirmed,
      overallConfidence: open > 0 ? 'medium' : 'high',
      bySource,
      pendingQuestions: [...new Set(list.flatMap((d) => d.pendingQuestions))],
    };
  });
  return { total: subjects.length, subjects };
}

export function buildInsights(projectId: string | null): RationalizationReport {
  const insights = [
    {
      systemId: scoped(projectId, 'sys-billing-legacy'),
      systemName: 'Faturamento Legado',
      category: 'redundancy',
      classification: 'eliminate',
      rationale: 'Capacidade de cobrança duplicada com o Gateway de Pagamento.',
      evidence: 'Ambos expõem "Cobrança"; legado está deprecated.',
      affectedDependentCount: 2,
      relatedSystemIds: [scoped(projectId, 'sys-payment')],
    },
    {
      systemId: scoped(projectId, 'sys-crm'),
      systemName: 'CRM Comercial',
      category: 'modernization',
      classification: 'tolerate',
      rationale: 'Sem duplicidade, porém revisão de arquitetura vencida.',
      evidence: 'Última revisão há mais de 12 meses.',
      affectedDependentCount: 1,
      relatedSystemIds: [],
    },
    {
      systemId: scoped(projectId, 'sys-analytics'),
      systemName: 'Plataforma de Analytics',
      category: 'cost',
      classification: 'invest',
      rationale: 'Alto custo, mas central para decisão corporativa.',
      evidence: 'Custo mensal 4200 USD; consumida por 2 domínios.',
      affectedDependentCount: 0,
      relatedSystemIds: [],
    },
  ];
  const byClassification: Record<string, number> = {};
  for (const i of insights) byClassification[i.classification] = (byClassification[i.classification] ?? 0) + 1;
  return {
    systemCount: SYSTEMS.length,
    insightCount: insights.length,
    byClassification,
    insights,
  };
}

export function buildPatterns(projectId: string | null): ArchitecturePattern[] {
  const now = '2026-07-01T00:00:00Z';
  return [
    {
      id: scoped(projectId, 'adr-001'),
      projectId,
      kind: 'adr',
      title: 'Adotar SSO corporativo para todos os sistemas',
      status: 'accepted',
      context: 'Múltiplos sistemas com autenticação própria elevam risco e custo.',
      body: 'Toda nova integração deve delegar autenticação ao IdP corporativo.',
      problem: 'Fragmentação de identidade entre sistemas.',
      consequences: 'Menor superfície de ataque; dependência do IdP central.',
      tags: ['segurança', 'identidade'],
      supersedesId: null,
      documentId: null,
      createdAt: now,
      updatedAt: now,
    },
    {
      id: scoped(projectId, 'adr-002'),
      projectId,
      kind: 'adr',
      title: 'Descomissionar Faturamento Legado',
      status: 'proposed',
      context: 'ERP em fim de vida com capacidade duplicada.',
      body: 'Migrar cobrança do legado para o Gateway de Pagamento até 2027.',
      problem: 'Manutenção cara de tecnologia obsoleta.',
      consequences: 'Redução de custo; esforço de migração de dados fiscais.',
      tags: ['racionalização', 'financeiro'],
      supersedesId: null,
      documentId: null,
      createdAt: now,
      updatedAt: now,
    },
    {
      id: scoped(projectId, 'pat-001'),
      projectId,
      kind: 'pattern',
      title: 'Padrão de integração assíncrona por eventos',
      status: 'accepted',
      context: 'Acoplamento síncrono degrada resiliência entre domínios.',
      body: 'Comunicação entre domínios deve preferir eventos e filas duráveis.',
      problem: null,
      consequences: 'Maior resiliência; complexidade de consistência eventual.',
      tags: ['integração', 'resiliência'],
      supersedesId: null,
      documentId: null,
      createdAt: now,
      updatedAt: now,
    },
  ];
}

export function buildBaselines(projectId: string): ArchitectureBaseline[] {
  const now = '2026-07-10T00:00:00Z';
  return [
    {
      id: scoped(projectId, 'baseline-1'),
      projectId,
      status: 'closed',
      title: 'Baseline entrega Q2',
      proposalId: scoped(projectId, 'prop-42'),
      baselineElementCount: 18,
      hasAsBuilt: true,
      createdAt: '2026-04-01T00:00:00Z',
      updatedAt: now,
    },
    {
      id: scoped(projectId, 'baseline-2'),
      projectId,
      status: 'open',
      title: 'Baseline entrega Q3',
      proposalId: null,
      baselineElementCount: 21,
      hasAsBuilt: false,
      createdAt: now,
      updatedAt: now,
    },
  ];
}

export function buildBaselineComparison(baselineId: string): BaselineComparison {
  const bare = baselineId.includes('::') ? baselineId.split('::').pop()! : baselineId;
  const closed = bare === 'baseline-1';
  return {
    baselineId,
    matched: closed ? 16 : 12,
    missing: closed ? 2 : 5,
    unplanned: closed ? 1 : 4,
    edgeMatched: closed ? 20 : 14,
    edgeMissing: closed ? 1 : 6,
    edgeUnplanned: closed ? 2 : 3,
    conformancePercent: closed ? 88.9 : 63.2,
    drifts: [
      { elementId: `${baselineId}::d1`, name: 'Serviço de Notificação', kind: 'container', drift: 'unplanned' },
      { elementId: `${baselineId}::d2`, name: 'Fila de Eventos', kind: 'component', drift: 'missing' },
    ],
  };
}

export function buildPortfolioReuse(
  projectId: string | null,
  capability: string | null,
  domain: string | null,
): PortfolioReuse {
  let seeds = SYSTEMS;
  if (capability) seeds = seeds.filter((s) => s.capabilities.includes(capability));
  if (domain) seeds = seeds.filter((s) => s.domain === domain);
  const candidates = seeds.map((s) => ({
    systemId: scoped(projectId, s.id),
    name: s.name,
    domain: s.domain,
    matchedCapabilities: capability
      ? s.capabilities.filter((c) => c === capability)
      : s.capabilities,
    duplicateFlag: s.capabilities.includes('Cobrança'),
  }));
  return {
    capability,
    domain,
    candidateCount: candidates.length,
    candidates,
  };
}
