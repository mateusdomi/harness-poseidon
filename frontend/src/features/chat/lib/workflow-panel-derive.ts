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

export function expectedArtifactsForPhase(phase: Phase): readonly string[] {
  return phase.deliverables.map((deliverable) => deliverable.name);
}

export function expectedArtifactHealth(phase: Phase, name: string): DocumentHealth {
  return (
    phase.deliverables.find((deliverable) => deliverable.name === name)?.status ??
    (phase.state === 'pending' ? 'planned' : 'notStarted')
  );
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
  _gates: readonly Gate[],
  _documents: readonly Document[],
  _tasks: readonly Task[],
  _approvals: readonly Approval[],
): PhaseProgressEvidence {
  // Mantidos na assinatura por compatibilidade com os consumidores; a fonte
  // canônica agora já chega agregada no próprio contrato da fase.
  void _gates;
  void _documents;
  void _tasks;
  void _approvals;
  return {
    tasks: phase.progress.tasks,
    documents: phase.progress.documents,
    gates: phase.progress.gates,
    approvals: { completed: 0, total: 0 },
    total: phase.progress.total,
    completed: phase.progress.completed,
    phaseStateFallback: phase.progress.total === 0,
    percent: phase.progress.percent,
  };
}

/**
 * Progresso 0–100 da fase, já calculado pelo read model único do workflow.
 * Os argumentos antigos permanecem por compatibilidade até a próxima revisão
 * do contrato público do helper.
 */
export function phaseProgress(
  phase: Phase,
  _gates: readonly Gate[],
  _documents: readonly Document[],
): number {
  void _gates;
  void _documents;
  return phase.progress.percent;
}
