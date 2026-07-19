import type { Document, Gate, Phase } from '@/api';

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
  | 'notProduced'
  | 'produced'
  | 'awaitingApproval'
  | 'approved'
  | 'rejected'
  | 'notApplicable';

export function documentHealth(doc: Document): DocumentHealth {
  if (doc.inconsistent || doc.state === 'outdated') return 'rejected';
  switch (doc.state) {
    case 'planned':
      return 'notProduced';
    case 'inElaboration':
    case 'inReview':
      return 'produced';
    case 'awaitingApproval':
      return 'awaitingApproval';
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
export const DOCUMENT_HEALTH_FILTERS = ['notProduced', 'produced', 'awaitingApproval'] as const;
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
    notProduced: 0,
    produced: 0,
    awaitingApproval: 0,
    approved: 0,
    rejected: 0,
    notApplicable: 0,
  };
  for (const doc of documents) counts[documentHealth(doc)] += 1;
  return counts;
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
  const phaseGates = gates.filter((gate) => gate.phaseId === phase.id);
  const phaseDocs = documentsOfPhase(documents, phase);
  const total = phaseGates.length + phaseDocs.length;
  if (total === 0) {
    return phase.state === 'completed' || phase.state === 'skipped' ? 100 : 0;
  }
  const done =
    phaseGates.filter((gate) => gate.state === 'approved' || gate.state === 'waived').length +
    phaseDocs.filter((doc) => doc.state === 'approved').length;
  return Math.round((done / total) * 100);
}
