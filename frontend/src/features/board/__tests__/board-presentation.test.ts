import {
  approvalPresentation,
  resolveBoardPresentation,
  taskStateLabelKey,
  unassignedLabelKey,
} from '@/features/board/lib/board-presentation';
import { fixtures } from '@/api/fixtures';

describe('board-presentation', () => {
  it('mantém detalhes e filtros internos fora da experiência de negócio', () => {
    expect(resolveBoardPresentation(false)).toEqual({
      showAdvancedFilters: false,
      showInternalId: false,
      showCardType: false,
      showTechnicalDetail: false,
    });
    expect(taskStateLabelKey('backlog', false)).toBe('board.presentation.states.backlog');
    expect(taskStateLabelKey('blocked', false)).toBe('board.presentation.states.blocked');
    expect(unassignedLabelKey(false)).toBe('board.card.unassigned');
  });

  it('preserva a projeção técnica autorizada sem alterar o contrato de domínio', () => {
    expect(resolveBoardPresentation(true)).toEqual({
      showAdvancedFilters: true,
      showInternalId: true,
      showCardType: true,
      showTechnicalDetail: true,
    });
    expect(taskStateLabelKey('testsGates', true)).toBe('status.taskState.testsGates');
    expect(unassignedLabelKey(true)).toBe('board.card.unassignedTechnical');
  });

  it('projeta aprovações por tipo sem expor texto operacional no modo de negócio', () => {
    const gateApproval = fixtures.data.approvals.find((approval) => approval.gateId)!;
    const translate = (key: string) => key;

    expect(approvalPresentation(gateApproval, false, translate)).toEqual({
      title: gateApproval.businessTitle,
      description: gateApproval.businessDescription,
      resolutionNote: null,
      kindKey: 'board.detail.approvals.businessKinds.validation',
      hidesOperationalNote: false,
      canResolve: true,
    });
    expect(approvalPresentation(gateApproval, true, translate)).toEqual({
      title: gateApproval.title,
      description: gateApproval.description,
      resolutionNote: gateApproval.resolutionNote,
      kindKey: null,
      hidesOperationalNote: false,
      canResolve: true,
    });

    expect(
      approvalPresentation(
        {
          ...gateApproval,
          resolutionNote: 'Gate recusado no CI; consultar 01ARZ3NDEKTSV4RRFFQ69G5FAV.',
        },
        false,
        translate,
      ),
    ).toMatchObject({ resolutionNote: null, hidesOperationalNote: true });

    expect(
      approvalPresentation(
        { ...gateApproval, businessTitle: null, businessDescription: null },
        false,
        translate,
      ),
    ).toMatchObject({
      canResolve: false,
      title: 'board.detail.approvals.businessPurposeUnavailableTitle',
    });
  });
});
