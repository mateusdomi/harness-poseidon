import type { TaskState } from '@/api';

export interface BoardPresentationPolicy {
  showAdvancedFilters: boolean;
  showInternalId: boolean;
  showCardType: boolean;
  showTechnicalDetail: boolean;
}

/**
 * Projeção central do Quadro. Os contratos e filtros técnicos continuam
 * disponíveis, mas só entram na composição visual quando o perfil autorizado
 * solicita detalhes técnicos.
 */
export function resolveBoardPresentation(showTechnicalDetails: boolean): BoardPresentationPolicy {
  return {
    showAdvancedFilters: showTechnicalDetails,
    showInternalId: showTechnicalDetails,
    showCardType: showTechnicalDetails,
    showTechnicalDetail: showTechnicalDetails,
  };
}

export function taskStateLabelKey(state: TaskState, showTechnicalDetails: boolean): string {
  return showTechnicalDetails ? `status.taskState.${state}` : `board.presentation.states.${state}`;
}

export function unassignedLabelKey(showTechnicalDetails: boolean): string {
  return showTechnicalDetails ? 'board.card.unassignedTechnical' : 'board.card.unassigned';
}
