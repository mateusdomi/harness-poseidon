import { screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import type { ProjectReliability } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import ReliabilityPage from '@/features/reliability/pages/reliability-page';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = createTestBundle().fixtures.data;
const projeto = fixtures.projects[0];

function reliability(overrides: Partial<ProjectReliability> = {}): ProjectReliability {
  return {
    projectId: projeto.id,
    classifiedAttempts: 4,
    sampleTruncated: false,
    intents: [
      {
        intent: 'conversa_geral',
        turns: 4,
        averageDurationMs: 820,
        p95DurationMs: 1200,
        turnsWithDroppedActions: 1,
        averageConfidence: 0.91,
      },
    ],
    subscriptions: [
      {
        accountAlias: 'assinatura-principal',
        providers: ['anthropic'],
        invocations: 10,
        tasksTouched: 4,
        successes: 8,
        totalTokens: 120_000,
        exactTokens: 110_000,
        estimatedTokens: 10_000,
        usageUnavailableInvocations: 1,
        estimatedCostUsd: 3.5,
        lastInvokedAt: '2026-07-31T12:00:00Z',
      },
    ],
    failureModesByCategory: { specificationanddesign: 3, verificationandtermination: 1 },
    advice: 'A concentração está na especificação: o enunciado precisa mudar.',
    capabilities: [
      {
        accountAlias: 'assinatura-principal',
        modelTier: 'opus',
        cardType: 'implementation',
        k: 3,
        tasksObserved: 4,
        passAt1: 0.5,
        passAtK: 0.75,
        recommendedMaxRounds: 2,
      },
    ],
    ...overrides,
  };
}

function renderPage(value: ProjectReliability) {
  const bundle = createTestBundle();
  vi.spyOn(bundle.api, 'getProjectReliability').mockResolvedValue(value);
  return renderWithApi(
    <MemoryRouter initialEntries={['/reliability']}>
      <Routes>
        <Route path="/reliability" element={<ReliabilityPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

/** A linha da tabela que contém o padrão — o nome acessível de uma `tr` não cobre suas células. */
async function findRow(pattern: RegExp) {
  return waitFor(() => {
    const linha = screen
      .getAllByRole('row')
      .find((candidate) => pattern.test(candidate.textContent ?? ''));
    expect(linha).toBeDefined();
    return linha!;
  });
}

describe('tela de produtividade da fleet', () => {
  it('mostra produtividade por assinatura com acerto e custo lado a lado', async () => {
    renderPage(reliability());

    const linha = await findRow(/US\$ 3\.50/);
    // 8 de 10 execuções com sucesso — a taxa é derivada, não um campo que o backend manda pronto.
    expect(linha).toHaveTextContent('80%');
    expect(linha).toHaveTextContent(`${(110_000).toLocaleString('pt-BR')} exatos`);
    expect(linha).toHaveTextContent(`${(10_000).toLocaleString('pt-BR')} estimados`);
    expect(linha).toHaveTextContent('1 sem usage');
  });

  it('mostra pass@1 e pass@k por par conta+modelo com a recomendação de rodadas', async () => {
    renderPage(reliability());

    await screen.findByText('pass@1');
    const capacidade = await findRow(/opus/);
    expect(capacidade).toHaveTextContent('50%');
    expect(capacidade).toHaveTextContent('75%');
  });

  it('AVISA quando a amostra foi recortada — um recorte silencioso se lê como "é tudo"', async () => {
    renderPage(reliability({ sampleTruncated: true }));

    await waitFor(() =>
      expect(screen.getByRole('status', { name: '' })).toHaveTextContent(/maior que a amostra/i),
    );
  });

  it('mostra a conversa da liderança por assunto, com os ajustes que o sistema fez', async () => {
    // B14: o turno é o laço mais quente do produto e era o único sem medição nenhuma.
    renderPage(reliability());

    const linha = await findRow(/Conversa geral/);
    expect(linha).toHaveTextContent('820 ms');
    expect(linha).toHaveTextContent('91%');
  });

  it('não inventa estado de erro quando o projeto ainda não tem execução', async () => {
    renderPage(reliability({ subscriptions: [], capabilities: [], intents: [], classifiedAttempts: 0 }));

    expect(await screen.findByText(/Ainda não há execução para medir/i)).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
