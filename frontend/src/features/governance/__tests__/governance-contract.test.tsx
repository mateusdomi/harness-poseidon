import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';

import { ApiError } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import GovernanceContractPage from '@/features/governance/pages/governance-contract-page';
import { renderWithApi } from '@/test/render-with-providers';

const receipt = {
  projectId: 'project-1', taskId: 'task-1', attemptId: 'attempt-1', turnId: 'turn-1', agentId: 'agent-1',
  manifestVersion: '1.0.0',
  documents: [{ documentId: 'gov-core', checksum: 'sha256:doc', selectionReason: 'core', loadPolicy: 'always', estimatedTokens: 800 }],
  estimatedTokens: 1200, actualPromptTokens: 1150, truncated: [], conflicts: [], cacheHits: 1,
  provider: 'claude', model: 'sonnet', timestamp: '2026-07-20T12:00:00Z',
  bundleChecksum: 'sha256:bundle', state: 'completed', gateResult: 'passed', version: 1,
};

const candidate = {
  organizationId: 'org-1', projectId: 'project-1', candidateId: 'candidate-1', type: 'rule' as const, state: 'candidate' as const,
  fingerprint: 'sha256:fingerprint', observation: 'Falhas transitórias recorrentes.',
  evidence: [{ kind: 'test', reference: 'evidence://test/1', checksum: 'sha256:evidence', summary: 'Teste isolado.' }],
  payload: { title: 'Retry seguro', statement: 'Retry limitado.', instructions: null, personaId: null, workflowId: null, toolId: null, documentId: null, providerId: null, modelId: null, refinement: null, recommendation: null, correction: null },
  actorAgentId: 'actor-1', actorProvider: 'provider-a', actorModel: 'actor-model', baselineVersion: 'rule/1', proposedVersion: 'rule/2',
  evaluatorAgentId: null, evaluatorProvider: null, evaluatorModel: null, evaluationVerdict: null, shadowResult: null,
  reviewerProfileId: null, decisionNote: null, activeVersion: null, previousVersion: null,
  createdAt: '2026-07-20T12:00:00Z', updatedAt: '2026-07-20T12:00:00Z', version: 1,
};

function renderContractPage(options?: { staleError?: Error }) {
  const bundle = createTestBundle();
  vi.spyOn(bundle.api, 'listGovernanceReceipts').mockResolvedValue([receipt]);
  vi.spyOn(bundle.api, 'listGovernanceMetrics').mockResolvedValue([{
    projectId: 'project-1', turnId: 'turn-1', eventId: 'event-1', kind: 'Delivered',
    documentId: 'gov-core', ruleId: null, detailCode: null, tokenCount: 800, occurredAt: '2026-07-20T12:00:01Z',
  }]);
  const staleSpy = vi.spyOn(bundle.api, 'listStaleDocumentFindings');
  if (options?.staleError) staleSpy.mockRejectedValue(options.staleError);
  else staleSpy.mockResolvedValue([{ findingId: 'finding-1', documentId: 'gov-core', kind: 'ReviewOverdue', detail: 'reviewDueAt expirou', recommendedTask: 'Revisar documento', detectedAt: '2026-07-20T12:00:00Z' }]);
  vi.spyOn(bundle.api, 'listHashlineBenchmark').mockResolvedValue([{ strategy: 'hashline', editSuccesses: 10, staleRejections: 2, retries: 1, estimatedTokens: 100, durationMicroseconds: 500, regressions: 0 }]);
  vi.spyOn(bundle.api, 'listAgentExecutors').mockResolvedValue([{ id: 'omp-rpc', available: false, enabled: false, executableName: null, license: 'MIT', availabilityReason: 'omp ausente' }]);
  vi.spyOn(bundle.api, 'getDiagnostics').mockResolvedValue({
    product: { name: 'Poseidon', version: '1.0', codename: 'Poseidon' }, apiMode: 'http', realtimeState: 'connected',
    checks: [{ key: 'api', state: 'ok', detail: 'respondendo' }], generatedAt: '2026-07-20T12:00:00Z',
  });
  vi.spyOn(bundle.api, 'listLearningCandidates').mockResolvedValue({ items: [candidate], nextCursor: null, total: 1 });
  vi.spyOn(bundle.api, 'getLearningCandidate').mockResolvedValue(candidate);
  vi.spyOn(bundle.api, 'listLearningCandidateEvidence').mockResolvedValue(candidate.evidence);
  vi.spyOn(bundle.api, 'compareLearningCandidate').mockResolvedValue({ baselineVersion: 'rule/1', proposedVersion: 'rule/2', proposedPayload: candidate.payload, activeVersion: null, previousVersion: null });
  vi.spyOn(bundle.api, 'listLearningCandidateHistory').mockResolvedValue([]);
  vi.spyOn(bundle.api, 'getLearningCandidateMetrics').mockResolvedValue({ created: 1, deduplicated: 0, rejected: 0, approved: 0, promoted: 0, rolledBack: 0, averageFirstPassSuccessDelta: 0, averageRepeatedErrorRateDelta: 0, tokenImpact: 0, averageCostPerAcceptedTaskDelta: 0, regressionsAfterPromotion: 0 });
  return renderWithApi(<GovernanceContractPage />, bundle);
}

describe('GovernanceContractPage P1/P2', () => {
  it('mostra sinal operacional, receipt reproduzível, métricas e a distinção do catálogo', async () => {
    const user = userEvent.setup();
    renderContractPage();

    expect(await screen.findByText('Sinal técnico: GO')).toBeInTheDocument();
    expect(screen.getByText('omp-rpc')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Bundles e receipts' }));
    expect(await screen.findByText('turn-1')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Ver bundle' }));
    expect(await screen.findByText('Context bundle reproduzível')).toBeInTheDocument();
    expect(await screen.findByText('Delivered')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Documentos e saúde' }));
    expect(screen.getByText('Catálogo canônico não publicado')).toBeInTheDocument();
    expect(screen.getByText('ReviewOverdue')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: 'Aprendizado P2' }));
    expect(await screen.findByText('Learning candidates')).toBeInTheDocument();
    expect(await screen.findByText('Retry seguro')).toBeInTheDocument();
  });

  it('abre evidência/comparação e só promove por confirmação manual', async () => {
    const user = userEvent.setup();
    const { bundle, queryClient } = renderContractPage();
    const transition = vi.spyOn(bundle.api, 'transitionLearningCandidate').mockResolvedValue({ ...candidate, state: 'promoted', version: 7 });

    await user.click(screen.getByRole('tab', { name: 'Aprendizado P2' }));
    await user.click(await screen.findByRole('button', { name: /Retry seguro/ }));
    expect(await screen.findByText('Evidências')).toBeInTheDocument();
    expect(screen.getByText('Comparação de versões')).toBeInTheDocument();
    expect(screen.getByText('Retry limitado.')).toBeInTheDocument();

    vi.spyOn(bundle.api, 'getLearningCandidate').mockResolvedValue({ ...candidate, state: 'approved', version: 6 });
    await queryClient.invalidateQueries({ queryKey: ['governance-runtime', 'learning-candidates'] });
    expect(await screen.findByText('approved')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Promover manualmente' }));
    const confirm = screen.getByRole('button', { name: 'Confirmar transição' });
    expect(confirm).toBeDisabled();
    await user.click(screen.getByText(/Confirmo a promoção manual/));
    expect(confirm).toBeEnabled();
    await user.click(confirm);

    await waitFor(() => expect(transition).toHaveBeenCalledWith('candidate-1', 'promotion', { expectedVersion: 6, note: null }));
  });

  it('submete o evaluator e identifica o resultado como apenas desta sessão', async () => {
    const user = userEvent.setup();
    const { bundle } = renderContractPage();
    const evaluate = vi.spyOn(bundle.api, 'createFreshContextEvaluation').mockResolvedValue({
      schemaVersion: '1.0', evaluationId: 'evaluation-1', verdict: 'FAIL', provider: 'claude', model: 'sonnet',
      readOnly: true, cleanContext: true, evaluatedAt: '2026-07-20T12:00:00Z',
      findings: [{ priority: 'P1', confidence: 0.95, evidence: 'Teste falhou', path: 'frontend/src/app.tsx', range: '10', ruleId: 'TEST-001', recommendedAction: 'Corrigir teste' }],
    });

    await user.click(screen.getByRole('tab', { name: 'Avaliação' }));
    for (const label of ['Projeto (projectId)', 'Tarefa (taskId)', 'Tentativa (attemptId)', 'Turno (turnId)', 'Agente executor', 'Agente avaliador']) {
      await user.type(screen.getByLabelText(label, { exact: false }), `${label}-1`);
    }
    await user.type(screen.getByLabelText('Critérios de aceite', { exact: false }), 'Sem regressão');
    await user.type(screen.getByLabelText('Diff', { exact: false }), '+ mudança');
    await user.type(screen.getByLabelText('Evidências', { exact: false }), 'npm test');
    await user.click(screen.getByRole('button', { name: 'Executar avaliação' }));

    await waitFor(() => expect(evaluate).toHaveBeenCalledOnce());
    const result = await screen.findByText('Resultado desta sessão');
    expect(within(result.closest('div')!.parentElement!).getByText('FAIL')).toBeInTheDocument();
    expect(screen.getByText('TEST-001')).toBeInTheDocument();
    expect(screen.getByText('Histórico e replay indisponíveis')).toBeInTheDocument();
  });

  it('expõe erro de autorização e permite tentar novamente', async () => {
    renderContractPage({ staleError: ApiError.of(401, 'Unauthorized', 'Sessão sem acesso à governança.') });
    expect(await screen.findByRole('alert')).toHaveTextContent('Sessão sem acesso à governança.');
    expect(screen.getByRole('button', { name: 'Tentar novamente' })).toBeInTheDocument();
  });
});
