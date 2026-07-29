import type { Approval, TaskState } from '@/api';

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

export interface ApprovalPresentation {
  title: string;
  description: string;
  resolutionNote: string | null;
  kindKey: string | null;
  hidesOperationalNote: boolean;
  canResolve: boolean;
}

/**
 * Approval copy is operational free text and may contain internal vocabulary.
 * Business mode therefore uses structured fields and the linked work title.
 * The complete operational copy remains available in authorized technical mode.
 */
export function approvalPresentation(
  approval: Approval,
  showTechnicalDetails: boolean,
  translate: (key: string) => string,
): ApprovalPresentation {
  if (showTechnicalDetails) {
    return {
      title: approval.title,
      description: approval.description,
      resolutionNote: approval.resolutionNote,
      kindKey: null,
      hidesOperationalNote: false,
      canResolve: true,
    };
  }

  const kind = approval.gateId
    ? 'validation'
    : approval.documentId
      ? 'document'
      : approval.taskId
        ? 'delivery'
        : 'decision';

  return {
    title:
      approval.businessTitle ??
      translate('board.detail.approvals.businessPurposeUnavailableTitle'),
    description:
      approval.businessDescription ??
      translate('board.detail.approvals.businessPurposeUnavailableDescription'),
    resolutionNote: null,
    kindKey: `board.detail.approvals.businessKinds.${kind}`,
    hidesOperationalNote: approval.resolutionNote !== null,
    canResolve:
      approval.businessTitle !== null &&
      approval.businessDescription !== null,
  };
}
