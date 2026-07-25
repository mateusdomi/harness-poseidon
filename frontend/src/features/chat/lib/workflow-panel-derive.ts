import type { Approval, Document, Gate, Phase, Task } from '@/api';

/**
 * Derivações puras do painel lateral de workflow do chat — sem React,
 * sem i18n (labels são chaves). Regras de domínio documentadas em
 * D-068 (conceitos documentais) e D-069 (progresso por fase).
 */

/**
 * Conceito documental exibido no painel (D-068): os 8 estados do contrato
 * + flag `inconsistent` são mapeados para 5 conceitos pedidos pela missão
 * mais um neutro (`notApplicable`) para os estados terminais neutros.
 *
 * - notProduced: `planned`;
 * - produced: `inElaboration`, `inReview` (existe conteúdo em trabalho);
 * - awaitingApproval: `awaitingApproval`;
 * - approved: `approved`;
 * - rejected: flag `inconsistent` (qualquer estado) OU `outdated`
 *   (desatualizado = inconsistente com o estado atual do projeto);
 * - notApplicable: `superseded`, `notApplicable`.
 *
 * A flag `inconsistent` tem precedência sobre o estado: um documento em
 * elaboração marcado inconsistente aparece como rejeitado/inconsistente.
 */
export type DocumentHealth =
  | 'planned'
  | 'notStarted'
  | 'inProduction'
  | 'produced'
  | 'inReview'
  | 'approved'
  | 'rejected'
  | 'notApplicable';

export function documentHealth(doc: Document): DocumentHealth {
  if (doc.inconsistent || doc.state === 'outdated') return 'rejected';
  switch (doc.state) {
    case 'planned':
      return 'planned';
    case 'inElaboration':
      return 'inProduction';
    case 'inReview':
      return 'produced';
    case 'awaitingApproval':
      return 'inReview';
    case 'approved':
      return 'approved';
    case 'superseded':
    case 'notApplicable':
      return 'notApplicable';
  }
}

/**
 * Chips de filtro rápido da fase (D-068): os 3 conceitos acionáveis,
 * na ordem do funil documental. `null` = sem filtro (todos).
 */
export const DOCUMENT_HEALTH_FILTERS = ['planned', 'inProduction', 'inReview'] as const;
export type DocumentHealthFilter = (typeof DOCUMENT_HEALTH_FILTERS)[number];

/** Documentos vinculados à fase (match por `phaseName`, contrato Document). */
export function documentsOfPhase(documents: readonly Document[], phase: Phase): Document[] {
  return documents.filter((doc) => doc.phaseName === phase.name);
}

export function filterDocumentsByHealth(
  documents: readonly Document[],
  filter: DocumentHealthFilter | null,
): Document[] {
  if (filter === null) return [...documents];
  return documents.filter((doc) => documentHealth(doc) === filter);
}

/** Contagem por conceito — usada nos chips (chip some com zero? não: mostra 0). */
export function countDocumentsByHealth(
  documents: readonly Document[],
): Record<DocumentHealth, number> {
  const counts: Record<DocumentHealth, number> = {
    planned: 0,
    notStarted: 0,
    inProduction: 0,
    produced: 0,
    inReview: 0,
    approved: 0,
    rejected: 0,
    notApplicable: 0,
  };
  for (const doc of documents) counts[documentHealth(doc)] += 1;
  return counts;
}

/** Artefatos canônicos esperados. São templates, não documentos existentes. */
const EXPECTED_ARTIFACTS: Readonly<Record<string, readonly string[]>> = {
  'Ideação e recebimento': ['Registro da solicitação', 'Visão inicial'],
  Descoberta: ['Visão', 'Stakeholders', 'Hipóteses', 'Riscos'],
  Requisitos: ['Requisitos', 'Critérios de aceite', 'Backlog', 'Rastreabilidade'],
  Arquitetura: [
    'Modelo C4',
    'ADRs',
    'Segurança',
    'Integrações',
    'Modelo de dados',
    'Plano de observabilidade',
  ],
  Planejamento: [
    'Roadmap',
    'Plano de releases',
    'Decomposição',
    'Dependências',
    'Plano de riscos',
    'Plano de testes',
  ],
  Implementação: ['Código', 'Migrations', 'Contratos', 'Documentação técnica', 'Evidências'],
  'Verificação e qualidade': [
    'Relatório de testes',
    'Revisão independente',
    'Segurança',
    'Performance',
    'Acessibilidade',
  ],
  'Prontidão para homologação': ['Checklist de prontidão para homologação'],
  Homologação: ['Roteiro de homologação', 'Evidências', 'Findings', 'Aceite'],
  'Prontidão para produção': [
    'Checklist de prontidão para produção',
    'Plano de implantação',
    'Plano de rollback',
  ],
  Produção: ['Runbook operacional', 'Registro de implantação'],
  Estabilização: ['Relatório de estabilização'],
  Sustentação: ['Plano de operação', 'Monitoramento', 'Registro de incidentes'],
  Encerramento: ['Dossiê de encerramento'],
  'Revisão de benefícios': ['Relatório de benefícios'],
  // Versão canônica anterior: permanece legível em runs imutáveis existentes.
  Recebimento: ['Registro da solicitação', 'Critérios de aceite'],
  Baseline: ['Baseline técnica', 'Mapa de dependências'],
  'Execução acompanhada': [
    'Código',
    'Migrations',
    'Contratos',
    'Documentação técnica',
    'Evidências',
    'Log de decisões',
  ],
  'Prontidão homolog': ['Checklist de prontidão para homologação'],
  'Prontidão prod': [
    'Checklist de prontidão para produção',
    'Plano de implantação',
    'Plano de rollback',
  ],
};

export function expectedArtifactsForPhase(phase: Phase): readonly string[] {
  return EXPECTED_ARTIFACTS[phase.name] ?? [];
}

export function absentArtifactHealth(phase: Phase): DocumentHealth {
  if (phase.state === 'skipped') return 'notApplicable';
  return phase.state === 'pending' ? 'planned' : 'notStarted';
}

export interface PhaseProgressEvidence {
  percent: number;
  completed: number;
  total: number;
  tasks: { completed: number; total: number };
  documents: { completed: number; total: number };
  gates: { completed: number; total: number };
  approvals: { completed: number; total: number };
  phaseStateFallback: boolean;
}

export function phaseProgressEvidence(
  phase: Phase,
  gates: readonly Gate[],
  documents: readonly Document[],
  tasks: readonly Task[],
  approvals: readonly Approval[],
): PhaseProgressEvidence {
  const phaseGates = gates.filter((gate) => gate.phaseId === phase.id);
  const phaseDocs = documentsOfPhase(documents, phase);
  const phaseTasks = tasks.filter((task) => task.phaseName === phase.name);
  const gateIds = new Set(phaseGates.map((gate) => gate.id));
  const taskIds = new Set(phaseTasks.map((task) => task.id));
  const phaseApprovals = approvals.filter(
    (approval) =>
      (approval.gateId !== null && gateIds.has(approval.gateId)) ||
      (approval.taskId !== null && taskIds.has(approval.taskId)),
  );

  const evidence = {
    tasks: {
      completed: phaseTasks.filter((task) => task.state === 'done').length,
      total: phaseTasks.length,
    },
    documents: {
      completed: phaseDocs.filter(
        (document) => document.state === 'approved' || document.state === 'notApplicable',
      ).length,
      total: phaseDocs.length,
    },
    gates: {
      completed: phaseGates.filter(
        (gate) => gate.state === 'approved' || gate.state === 'waived',
      ).length,
      total: phaseGates.length,
    },
    approvals: {
      completed: phaseApprovals.filter((approval) => approval.state === 'approved').length,
      total: phaseApprovals.length,
    },
  };
  const total =
    evidence.tasks.total +
    evidence.documents.total +
    evidence.gates.total +
    evidence.approvals.total;
  const completed =
    evidence.tasks.completed +
    evidence.documents.completed +
    evidence.gates.completed +
    evidence.approvals.completed;
  const phaseStateFallback = total === 0;

  return {
    ...evidence,
    total,
    completed,
    phaseStateFallback,
    percent: phaseStateFallback
      ? phase.state === 'completed' || phase.state === 'skipped'
        ? 100
        : 0
      : Math.round((completed / total) * 100),
  };
}

/**
 * Progresso 0–100 da fase (D-069) — o contrato de Phase NÃO tem
 * percentual pronto; derivação honesta a partir dos dados do run:
 *
 * - Itens concluíveis da fase = gates dela + documentos vinculados a ela.
 *   Concluído = gate `approved`/`waived` ou documento `approved`.
 * - Se a fase tem itens: percentual = concluídos / total (arredondado).
 * - Se NÃO tem itens: fallback pelo estado — `completed`/`skipped` = 100,
 *   demais = 0 (nenhum valor intermediário inventado).
 */
export function phaseProgress(
  phase: Phase,
  gates: readonly Gate[],
  documents: readonly Document[],
): number {
  return phaseProgressEvidence(phase, gates, documents, [], []).percent;
}
