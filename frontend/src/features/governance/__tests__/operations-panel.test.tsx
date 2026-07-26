import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';

import { createTestBundle } from '@/api/__tests__/test-utils';
import { OperationsPanel } from '@/features/governance/components/operations-panel';
import { renderWithApi } from '@/test/render-with-providers';

/**
 * Aba Operação (fases 5/6/10/12): cada bloco reflete um fato do backend real —
 * recomendações estatísticas, contenção do merge, reconciliação do ledger e memória
 * semântica com citações. O teste prova que a tela consome os métodos reais da API
 * e apresenta os números sem inventar nada.
 */
function renderPanel() {
  const bundle = createTestBundle();
  vi.spyOn(bundle.api, 'list').mockResolvedValue({
    items: [{ id: '01ARZ3NDEKTSV4RRFFQ69G5PRJ', name: 'Poseidon' }],
    nextCursor: null,
  } as never);
  vi.spyOn(bundle.api, 'listEvaluationRecommendations').mockResolvedValue({
    projectId: '01ARZ3NDEKTSV4RRFFQ69G5PRJ',
    aggregates: [
      {
        targetId: 'prod-alpha',
        model: 'gpt-5-codex',
        provider: 'codex',
        sampleSize: 3,
        successCount: 3,
        failureCount: 0,
        passRate: 1,
        compositeScore: 1,
        confidenceIntervalLower: 0.4385,
        confidenceIntervalUpper: 1,
        sampleSizeQualified: true,
      },
    ],
    recommendations: [
      {
        targetId: 'prod-alpha',
        model: 'gpt-5-codex',
        provider: 'codex',
        action: 'maintain_current_routing',
        score: 1,
        confidenceIntervalLower: 0.4385,
        confidenceIntervalUpper: 1,
        sampleSize: 3,
        recommendationReason: 'performance_within_normal_operating_parameters',
        generatedAt: '2026-07-26T12:00:00Z',
      },
    ],
  });
  vi.spyOn(bundle.api, 'getMergeContention').mockResolvedValue({
    enqueued: 4,
    serialized: 4,
    contended: 1,
    waiting: 0,
    active: 0,
    totalWaitMs: 12.5,
    maximumWaitMs: 8.2,
    contentionRatio: 0.25,
  });
  vi.spyOn(bundle.api, 'reconcileLedger').mockResolvedValue({
    tenantId: '01ARZ3NDEKTSV4RRFFQ69G5TEN',
    totalEntries: 42,
    isChainValid: true,
    tamperedCount: 0,
    discrepancySequenceNumbers: [],
    lastValidHash: 'A'.repeat(64),
    reconciledAt: '2026-07-26T12:00:00Z',
  });
  vi.spyOn(bundle.api, 'searchMemory').mockResolvedValue({
    snapshotId: 'snap1234abcd',
    snapshotHash: 'b'.repeat(64),
    totalTokens: 37,
    slices: [
      {
        documentId: '01ARZ3NDEKTSV4RRFFQ69G5ATT',
        documentType: 'solicitation_attachment',
        projectId: '01ARZ3NDEKTSV4RRFFQ69G5PRJ',
        content: 'relatorio.md: Requisito 1: exportar CSV.',
        score: 0.9137,
        citationReference: 'solicitation_attachment:01ARZ3NDEKTSV4RRFFQ69G5ATT',
        provenance: { fileName: 'relatorio.md', solicitationId: '01ARZ3NDEKTSV4RRFFQ69G5SOL' },
      },
    ],
  });
  renderWithApi(<OperationsPanel />, bundle);
  return { bundle };
}

describe('OperationsPanel (fases 5/6/10/12)', () => {
  it('apresenta recomendações reais com IC, contenção de merge e reconciliação sob demanda', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPanel();

    // Fase 5 — a tabela deriva do backend: agente, amostra, IC e a ação tipada.
    expect(await screen.findByText('prod-alpha')).toBeInTheDocument();
    expect(screen.getByText('3/3')).toBeInTheDocument();
    expect(screen.getByText('0.44–1.00')).toBeInTheDocument();
    expect(screen.getByText('Manter roteamento')).toBeInTheDocument();

    // Fase 10 — contenção medida do merge serializado.
    expect(await screen.findByText('Contenção do merge serializado')).toBeInTheDocument();
    expect(screen.getByText('25.0%')).toBeInTheDocument();

    // Fase 12 — reconciliação sob demanda com veredito da cadeia.
    await user.click(screen.getByRole('button', { name: 'Reconciliar agora' }));
    expect(await screen.findByText('Cadeia íntegra')).toBeInTheDocument();
    expect(screen.getByText('42 evento(s) verificados')).toBeInTheDocument();
    expect(bundle.api.reconcileLedger).toHaveBeenCalledTimes(1);

    // Fase 6 — busca na memória com citação e proveniência.
    await user.type(
      screen.getByLabelText('Buscar na memória'),
      'relatorio de auditoria csv',
    );
    await user.click(screen.getByRole('button', { name: 'Buscar' }));
    expect(
      await screen.findByText('relatorio.md: Requisito 1: exportar CSV.'),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/solicitation_attachment:01ARZ3NDEKTSV4RRFFQ69G5ATT/),
    ).toBeInTheDocument();
    expect(bundle.api.searchMemory).toHaveBeenCalledWith('relatorio de auditoria csv');
  });
});
