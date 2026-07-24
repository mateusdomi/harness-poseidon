import type {
  ArchitectureElement,
  ArchitectureRelationship,
  ArchitectureView,
} from './types';

/**
 * Modelo arquitetural determinístico de demonstração (modo mock / testes).
 * Cobre C4 (contexto → deployment), ArchiMate (negócio → motivação) e as
 * vistas extras (dados, segurança, infra, rede, custo, observabilidade)
 * para que TODA view do Studio tenha conteúdo sem backend.
 */

export interface ArchitectureFixtureStore {
  elements: ArchitectureElement[];
  relationships: ArchitectureRelationship[];
  views: ArchitectureView[];
}

function el(
  id: string,
  projectId: string,
  kind: string,
  name: string,
  description: string,
  properties: Record<string, string> = {},
  overrides: Partial<ArchitectureElement> = {},
): ArchitectureElement {
  return {
    id: `${projectId}::${id}`,
    projectId,
    kind,
    name,
    description,
    properties,
    state: 'baseline',
    locked: false,
    version: 1,
    ...overrides,
  };
}

function rel(
  id: string,
  projectId: string,
  sourceId: string,
  targetId: string,
  kind: string,
  properties: Record<string, string> = {},
): ArchitectureRelationship {
  return {
    id: `${projectId}::${id}`,
    projectId,
    sourceId: `${projectId}::${sourceId}`,
    targetId: `${projectId}::${targetId}`,
    kind,
    properties,
    state: 'baseline',
    version: 1,
  };
}

export function buildArchitectureFixture(projectId: string): ArchitectureFixtureStore {
  const elements: ArchitectureElement[] = [
    // Motivação (ArchiMate)
    el('stakeholder-owner', projectId, 'stakeholder', 'Dono do Produto', 'Stakeholder responsável pela visão.'),
    el('goal-autonomy', projectId, 'goal', 'Meta: Autonomia do Chefe', 'Loop autônomo provado end-to-end.'),
    el('req-proofs', projectId, 'requirement', 'Requisito: Provas', 'Toda mudança exige evidência versionada.'),

    // Contexto C4
    el('person-client', projectId, 'person', 'Cliente', 'Usuário final que consome o produto.'),
    el('sys-poseidon', projectId, 'softwareSystem', 'Poseidon', 'Control plane de agentes durável.'),
    el('sys-payment', projectId, 'softwareSystem', 'Gateway de Pagamento', 'Sistema externo de cobrança.', {
      external: 'true',
      cost: '1200',
    }),

    // Negócio (ArchiMate)
    el('biz-delivery', projectId, 'businessProcess', 'Processo de Entrega', 'Fluxo de entrega de software.'),
    el('biz-pm', projectId, 'businessActor', 'Gerente de Produto', 'Ator de negócio que prioriza demandas.'),

    // Aplicação / Containers
    el('app-frontend', projectId, 'container', 'Frontend Web', 'SPA React do Poseidon.', { tech: 'React' }),
    el('app-api', projectId, 'container', 'API Backend', 'API .NET do control plane.', { tech: '.NET' }),
    el('app-chat', projectId, 'applicationComponent', 'Serviço de Chat', 'Componente de conversa com o Chefe.'),

    // Componentes
    el('cmp-orchestrator', projectId, 'component', 'Orquestrador', 'Coordena tarefas dos agentes.'),
    el('cmp-projects', projectId, 'component', 'Controlador de Projetos', 'Endpoints REST de projetos.'),

    // Dados
    el('data-project', projectId, 'dataObject', 'Objeto de Dados Projeto', 'Entidade de domínio Projeto.', {
      tags: 'data,pii',
      retention: '5y',
    }),
    el('db-main', projectId, 'database', 'Banco Principal', 'PostgreSQL do control plane.', {
      tags: 'data',
      pii: 'true',
      cost: '800',
    }),

    // Tecnologia / Deployment / Infra
    el('tech-runtime', projectId, 'systemSoftware', 'Runtime .NET', 'Runtime de execução do backend.'),
    el('node-cluster', projectId, 'deploymentNode', 'Cluster K8s', 'Cluster de produção.', {
      tags: 'infrastructure,network',
      cost: '3000',
    }),

    // Rede / Segurança
    el('net-dmz', projectId, 'network', 'Sub-rede DMZ', 'Segmento de rede de borda.', { tags: 'network' }),
    el('sec-boundary', projectId, 'trustBoundary', 'Fronteira de Confiança', 'Perímetro de segurança da API.', {
      tags: 'security',
      trustBoundary: 'true',
    }),

    // Observabilidade
    el('obs-dashboard', projectId, 'dashboard', 'Painel de Observabilidade', 'Métricas e logs do sistema.', {
      tags: 'observability',
    }),
  ];

  const relationships: ArchitectureRelationship[] = [
    rel('r1', projectId, 'person-client', 'sys-poseidon', 'uses'),
    rel('r2', projectId, 'sys-poseidon', 'sys-payment', 'calls'),
    rel('r3', projectId, 'app-frontend', 'app-api', 'calls'),
    rel('r4', projectId, 'app-api', 'app-chat', 'contains'),
    rel('r5', projectId, 'app-api', 'cmp-orchestrator', 'contains'),
    rel('r6', projectId, 'app-api', 'cmp-projects', 'contains'),
    rel('r7', projectId, 'cmp-projects', 'db-main', 'reads-writes'),
    rel('r8', projectId, 'app-api', 'db-main', 'persists'),
    rel('r9', projectId, 'db-main', 'data-project', 'stores'),
    rel('r10', projectId, 'app-api', 'tech-runtime', 'runs-on'),
    rel('r11', projectId, 'tech-runtime', 'node-cluster', 'deployed-on'),
    rel('r12', projectId, 'node-cluster', 'net-dmz', 'attached-to'),
    rel('r13', projectId, 'sec-boundary', 'app-api', 'protects'),
    rel('r14', projectId, 'obs-dashboard', 'app-api', 'observes'),
    rel('r15', projectId, 'biz-pm', 'biz-delivery', 'performs'),
    rel('r16', projectId, 'biz-delivery', 'sys-poseidon', 'realized-by'),
    rel('r17', projectId, 'goal-autonomy', 'req-proofs', 'refines'),
    rel('r18', projectId, 'stakeholder-owner', 'goal-autonomy', 'holds'),
  ];

  const contextView: ArchitectureView = {
    id: `${projectId}::view-context`,
    projectId,
    name: 'C4 — Contexto do Sistema',
    description: 'Sistema Poseidon, seus usuários e sistemas externos.',
    notation: 'c4',
    elements: elements.filter((e) =>
      [`${projectId}::person-client`, `${projectId}::sys-poseidon`, `${projectId}::sys-payment`].includes(
        e.id,
      ),
    ),
    relationships: relationships.filter((r) => r.id === `${projectId}::r1` || r.id === `${projectId}::r2`),
  };

  const motivationView: ArchitectureView = {
    id: `${projectId}::view-motivation`,
    projectId,
    name: 'ArchiMate — Motivação',
    description: 'Stakeholder, metas e requisitos.',
    notation: 'archimate',
    elements: elements.filter((e) =>
      [
        `${projectId}::stakeholder-owner`,
        `${projectId}::goal-autonomy`,
        `${projectId}::req-proofs`,
      ].includes(e.id),
    ),
    relationships: relationships.filter(
      (r) => r.id === `${projectId}::r17` || r.id === `${projectId}::r18`,
    ),
  };

  return { elements, relationships, views: [contextView, motivationView] };
}
