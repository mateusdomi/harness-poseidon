import { describe, expect, it, vi } from 'vitest';

import { HttpApiClient } from '../client';

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
