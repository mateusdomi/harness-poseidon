import { afterEach, describe, expect, it, vi } from 'vitest';

import { HttpApiClient } from '../client';
import { API_REQUEST_EVENT, type ApiRequestTelemetry } from '../request-observability';

afterEach(() => {
  vi.useRealTimers();
});

const account = {
  id: '01ARZ3NDEKTSV4RRFFQ69G5FH2',
  providerId: '01ARZ3NDEKTSV4RRFFQ69G5FG2',
  label: 'Conta de teste',
  state: 'disabled',
  quotaLimitUsd: 100,
  quotaUsedUsd: 0,
  identity: 'billing@example.com',
  plan: 'pro',
  authentication: 'apiKey',
  health: 'healthy',
  quotaWindow: 'monthly',
  quotaResetsAt: null,
  capabilities: ['chat'],
};

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

describe('HttpApiClient — término seguro e observabilidade', () => {
  it('encerra GET travado com 504, publica slow/settled e não expõe query string', async () => {
    vi.useFakeTimers();
    const telemetry: ApiRequestTelemetry[] = [];
    const listener = (event: Event) => telemetry.push((event as CustomEvent<ApiRequestTelemetry>).detail);
    window.addEventListener(API_REQUEST_EVENT, listener);
    const fetchFn = vi.fn<typeof fetch>().mockImplementation((_input, init) => new Promise((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    }));
    const client = new HttpApiClient({ baseUrl: '', fetchFn, slowRequestMs: 5, readTimeoutMs: 10 });

    const assertion = expect(client.list('projects', { filter: { secret: 'never-log-this' } }))
      .rejects.toMatchObject({ problem: { status: 504 } });
    await vi.advanceTimersByTimeAsync(11);
    await assertion;

    expect(telemetry.map((item) => item.phase)).toEqual(['started', 'slow', 'settled']);
    expect(telemetry.every((item) => item.path === '/api/v1/projects')).toBe(true);
    expect(telemetry.at(-1)?.status).toBe(504);
    window.removeEventListener(API_REQUEST_EVENT, listener);
  });

  it('não aborta escrita longa e a conclui normalmente', async () => {
    vi.useFakeTimers();
    const fetchFn = vi.fn<typeof fetch>().mockImplementation((_input, init) => new Promise((resolve) => {
      expect(init?.signal).toBeUndefined();
      setTimeout(() => resolve(new Response(null, { status: 204 })), 20);
    }));
    const client = new HttpApiClient({ baseUrl: '', fetchFn, slowRequestMs: 5, readTimeoutMs: 10 });

    const removal = client.remove('projects', account.id);
    await vi.advanceTimersByTimeAsync(21);
    await expect(removal).resolves.toBeUndefined();
  });

  it('converte JSON inválido em erro explícito 502', async () => {
    const response = new Response('{', { status: 200, headers: { 'content-type': 'application/json' } });
    const client = new HttpApiClient({ baseUrl: '', fetchFn: vi.fn<typeof fetch>().mockResolvedValue(response) });
    await expect(client.list('projects')).rejects.toMatchObject({ problem: { status: 502 } });
  });
});

describe('HttpApiClient — lifecycle de contas', () => {
  it.each([
    ['enableAccount', 'active'],
    ['disableAccount', 'disabled'],
  ] as const)('%s usa PATCH /accounts/{id} com o estado do OpenAPI', async (method, state) => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({ ...account, state }));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test/', fetchFn });

    await client[method](account.id);

    expect(fetchFn).toHaveBeenCalledOnce();
    expect(fetchFn).toHaveBeenCalledWith(
      `https://api.example.test/api/v1/accounts/${account.id}`,
      expect.objectContaining({
        method: 'PATCH',
        credentials: 'include',
        body: JSON.stringify({ state }),
      }),
    );
  });

  it('cria conta enviando referência segura, sem persistir segredo no cliente', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(account));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    const created = await client.createAccount({
      providerId: account.providerId,
      label: account.label,
      credentialReference: 'keychain://poseidon/openai',
      identity: account.identity,
      plan: 'pro',
      authentication: 'apiKey',
      quotaWindow: 'monthly',
      capabilities: ['chat'],
    });

    expect(fetchFn).toHaveBeenCalledWith(
      'https://api.example.test/api/v1/accounts',
      expect.objectContaining({ method: 'POST' }),
    );
    expect(created).not.toHaveProperty('credentialReference');
  });
});

describe('HttpApiClient — definições de agentes V3', () => {
  it('adapta campos editoriais ao write contract e envia versão esperada', async () => {
    const response = {
      id: account.id,
      key: 'reviewer',
      name: 'Revisor',
      role: 'specialist',
      specialty: null,
      description: 'Revisa entregas.',
      defaultModelId: null,
      skillIds: [],
      toolIds: [],
      persona: null,
      mission: null,
      operatingPrinciples: ['Verificar evidências'],
      deliverables: ['Parecer técnico'],
      qualityCriteria: ['Sem regressões'],
      communicationStyle: null,
      limitations: ['Não publicar'],
      version: 3,
      enabled: true,
      archivedAt: null,
    };
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(response));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    await client.updateAgentDefinition(account.id, {
      key: 'reviewer',
      name: 'Revisor',
      role: 'specialist',
      description: 'Revisa entregas.',
      instructions: 'Verificar evidências',
      responsibilities: 'Parecer técnico',
      bestPractices: 'Sem regressões',
      restrictions: 'Não publicar',
      expectedVersion: 2,
    });

    const init = fetchFn.mock.calls[0][1]!;
    expect(init.method).toBe('PATCH');
    expect(JSON.parse(String(init.body))).toEqual(
      expect.objectContaining({
        operatingPrinciples: ['Verificar evidências'],
        deliverables: ['Parecer técnico'],
        qualityCriteria: ['Sem regressões'],
        limitations: ['Não publicar'],
        expectedVersion: 2,
      }),
    );
  });

  it('duplica com chave e nome exigidos pelo OpenAPI', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({}));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    await client.duplicateAgentDefinition(account.id, {
      key: 'reviewer-copia',
      name: 'Revisor (cópia)',
    });

    expect(fetchFn).toHaveBeenCalledWith(
      `https://api.example.test/api/v1/agent-definitions/${account.id}/duplicate`,
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({ key: 'reviewer-copia', name: 'Revisor (cópia)' }),
      }),
    );
  });
});

describe('HttpApiClient — governança P1', () => {
  const receipt = {
    projectId: 'project-1', taskId: 'task-1', attemptId: 'attempt-1', turnId: 'turn-1', agentId: 'agent-1',
    manifestVersion: '1.0.0', documents: [], estimatedTokens: 120, actualPromptTokens: null,
    truncated: [], conflicts: [], cacheHits: 1, provider: 'claude', model: null,
    timestamp: '2026-07-20T12:00:00Z', bundleChecksum: 'sha256:bundle', state: 'completed', gateResult: 'passed', version: 1,
  };

  it('lista receipts com os nomes de query publicados e valida a resposta', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse([receipt]));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    const result = await client.listGovernanceReceipts({ projectId: 'project-1', cursor: 'cursor-1', limit: 25 });

    expect(result[0].estimatedTokens).toBe(120);
    expect(fetchFn).toHaveBeenCalledWith(
      'https://api.example.test/api/v1/governance-runtime/receipts?projectId=project-1&cursor=cursor-1&limit=25',
      expect.objectContaining({ method: 'GET', credentials: 'include' }),
    );
  });

  it('consulta métricas e executores nos paths exatos do contrato', async () => {
    const fetchFn = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse([]))
      .mockResolvedValueOnce(jsonResponse([]));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    await client.listGovernanceMetrics('turn/encoded');
    await client.listAgentExecutors();

    expect(fetchFn.mock.calls[0][0]).toBe('https://api.example.test/api/v1/governance-runtime/receipts/turn%2Fencoded/metrics');
    expect(fetchFn.mock.calls[1][0]).toBe('https://api.example.test/api/v1/governance-runtime/executors');
  });

  it('envia avaliação tipada sem campos adicionais', async () => {
    const response = {
      schemaVersion: '1.0', evaluationId: 'evaluation-1', verdict: 'PASS', findings: [],
      provider: 'claude', model: null, readOnly: true, cleanContext: true, evaluatedAt: '2026-07-20T12:00:00Z',
    };
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse(response));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });
    const input = {
      evaluationId: 'evaluation-1', projectId: 'project-1', taskId: 'task-1', attemptId: 'attempt-1', turnId: 'turn-1',
      actorAgentId: 'actor-1', evaluatorAgentId: 'evaluator-1', riskTier: 'medium',
      acceptanceCriteria: ['sem regressão'], diff: '+ mudança', evidence: ['testes'], testResults: [],
    };

    await client.createFreshContextEvaluation(input);

    expect(fetchFn).toHaveBeenCalledWith(
      'https://api.example.test/api/v1/governance-runtime/evaluations',
      expect.objectContaining({ method: 'POST', body: JSON.stringify(input) }),
    );
  });

  it('falha fechado quando a resposta diverge do schema P1', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse([{ turnId: 'incompleto' }]));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });
    await expect(client.listGovernanceReceipts()).rejects.toThrow();
  });
});

describe('HttpApiClient — governança de aprendizado P2', () => {
  const candidate = {
    organizationId: 'org-1', projectId: 'project-1', candidateId: 'candidate-1', type: 'rule', state: 'candidate',
    fingerprint: 'sha256:fingerprint', observation: 'Falhas transitórias recorrentes.',
    evidence: [{ kind: 'test', reference: 'evidence://test/1', checksum: 'sha256:evidence', summary: 'Teste isolado.' }],
    payload: { title: 'Retry seguro', statement: 'Retry limitado.', instructions: null, personaId: null, workflowId: null, toolId: null, documentId: null, providerId: null, modelId: null, refinement: null, recommendation: null, correction: null },
    actorAgentId: 'actor-1', actorProvider: 'provider-a', actorModel: 'actor-model', baselineVersion: 'rule/1', proposedVersion: 'rule/2',
    evaluatorAgentId: null, evaluatorProvider: null, evaluatorModel: null, evaluationVerdict: null, shadowResult: null,
    reviewerProfileId: null, decisionNote: null, activeVersion: null, previousVersion: null,
    createdAt: '2026-07-20T12:00:00Z', updatedAt: '2026-07-20T12:00:00Z', version: 1,
  };

  it('lista com paginação e filtros canônicos e valida o envelope', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({ items: [candidate], nextCursor: 'next-1', total: '2' }));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    const page = await client.listLearningCandidates({ projectId: 'project-1', type: 'rule', state: 'candidate', cursor: 'cursor-1', limit: 30 });

    expect(page.total).toBe(2);
    expect(page.nextCursor).toBe('next-1');
    expect(fetchFn.mock.calls[0][0]).toBe('https://api.example.test/api/v1/governance-runtime/learning-candidates?projectId=project-1&type=rule&state=candidate&cursor=cursor-1&limit=30');
  });

  it('consulta detalhe, evidência, comparação, histórico e métricas nos paths publicados', async () => {
    const fetchFn = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(jsonResponse(candidate))
      .mockResolvedValueOnce(jsonResponse(candidate.evidence))
      .mockResolvedValueOnce(jsonResponse({ baselineVersion: 'rule/1', proposedVersion: 'rule/2', proposedPayload: candidate.payload, activeVersion: null, previousVersion: null }))
      .mockResolvedValueOnce(jsonResponse([]))
      .mockResolvedValueOnce(jsonResponse({ created: 1, deduplicated: 0, rejected: 0, approved: 0, promoted: 0, rolledBack: 0, averageFirstPassSuccessDelta: 0, averageRepeatedErrorRateDelta: 0, tokenImpact: 0, averageCostPerAcceptedTaskDelta: 0, regressionsAfterPromotion: 0 }));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    await client.getLearningCandidate('candidate/encoded');
    await client.listLearningCandidateEvidence('candidate/encoded');
    await client.compareLearningCandidate('candidate/encoded');
    await client.listLearningCandidateHistory('candidate/encoded');
    await client.getLearningCandidateMetrics({ projectId: 'project-1' });

    expect(fetchFn.mock.calls.map((call) => call[0])).toEqual([
      'https://api.example.test/api/v1/governance-runtime/learning-candidates/candidate%2Fencoded',
      'https://api.example.test/api/v1/governance-runtime/learning-candidates/candidate%2Fencoded/evidence',
      'https://api.example.test/api/v1/governance-runtime/learning-candidates/candidate%2Fencoded/compare',
      'https://api.example.test/api/v1/governance-runtime/learning-candidates/candidate%2Fencoded/history',
      'https://api.example.test/api/v1/governance-runtime/learning-candidates/metrics?projectId=project-1',
    ]);
  });

  it('envia OCC e Idempotency-Key em transições; promoção é chamada somente por ação explícita', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({ ...candidate, state: 'promoted', version: 7 }));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });

    await client.transitionLearningCandidate('candidate-1', 'promotion', { expectedVersion: 6, note: 'promoção manual' });

    const [url, init] = fetchFn.mock.calls[0];
    expect(url).toBe('https://api.example.test/api/v1/governance-runtime/learning-candidates/candidate-1/promotion');
    expect(init).toEqual(expect.objectContaining({ method: 'POST', credentials: 'include', body: JSON.stringify({ expectedVersion: 6, note: 'promoção manual' }) }));
    expect(new Headers(init?.headers).get('Idempotency-Key')).toMatch(/^ui-promotion-/);
  });

  it('falha fechado quando estado, enum ou payload P2 diverge', async () => {
    const fetchFn = vi.fn<typeof fetch>().mockResolvedValue(jsonResponse({ items: [{ ...candidate, state: 'auto_promoted' }], nextCursor: null, total: 1 }));
    const client = new HttpApiClient({ baseUrl: 'https://api.example.test', fetchFn });
    await expect(client.listLearningCandidates()).rejects.toThrow();
  });
});
