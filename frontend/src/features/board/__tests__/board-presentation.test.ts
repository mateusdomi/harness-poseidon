import {
  resolveBoardPresentation,
  taskStateLabelKey,
  unassignedLabelKey,
} from '@/features/board/lib/board-presentation';

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
});
