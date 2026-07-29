import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  accountBudget,
  consumptionPct,
  consumptionTone,
  modelDisplayName,
  nextBudgetReset,
} from '@/features/providers/lib/providers-derive';
import ProvidersPage from '@/features/providers/pages/providers-page';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

const fixtures = createTestBundle().fixtures.data;

function renderPage(mode: 'business' | 'technical' = 'technical') {
  const bundle = createTestBundle();
  useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
  usePresentationStore.setState({ modeByProfile: {} });
  usePresentationStore.getState().requestMode(bundle.fixtures.meta.currentProfileId, mode);
  return renderWithApi(
    <MemoryRouter initialEntries={['/providers']}>
      <Routes>
        <Route path="/providers" element={<ProvidersPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('providers-derive', () => {
  it('próximo reset: diário amanhã, semanal na próxima segunda, mensal no dia 1', () => {
    // 17/07/2026 é sexta-feira.
    const now = new Date(2026, 6, 17, 15, 30);
    expect(nextBudgetReset('daily', now)).toEqual(new Date(2026, 6, 18));
    expect(nextBudgetReset('weekly', now)).toEqual(new Date(2026, 6, 20));
    expect(nextBudgetReset('monthly', now)).toEqual(new Date(2026, 7, 1));
    // Segunda-feira: próxima segunda é +7 (nunca o próprio dia).
    expect(nextBudgetReset('weekly', new Date(2026, 6, 20, 9))).toEqual(new Date(2026, 6, 27));
  });

  it('percentual de consumo e tom semântico', () => {
    expect(consumptionPct(40, 100)).toBe(40);
    expect(consumptionPct(10, null)).toBeNull();
    expect(consumptionTone(50)).toBe('brand');
    expect(consumptionTone(80)).toBe('warning');
    expect(consumptionTone(120)).toBe('error');
    expect(consumptionTone(null)).toBe('brand');
  });

  it('budget da conta e nome de modelo', () => {
    const account = fixtures.accounts.find((a) => a.label === 'Conta secundária')!;
    const budget = accountBudget(account, fixtures.budgets);
    expect(budget?.scope).toBe('account');
    expect(accountBudget(fixtures.accounts[0], fixtures.budgets)).toBeNull();

    const model = fixtures.models[0];
    expect(modelDisplayName(model.id, fixtures.models)).toBe(model.displayName);
    expect(modelDisplayName('inexistente', fixtures.models)).toBe('inexistente');
  });
});

describe('ProvidersPage', () => {
  it('não expõe conexões e modelos no modo Negócio', () => {
    renderPage('business');

    expect(screen.getByText('Área disponível no modo Técnico')).toBeInTheDocument();
    expect(screen.queryByText('OpenAI')).not.toBeInTheDocument();
    expect(screen.queryByText('Conta principal')).not.toBeInTheDocument();
  });

  it('renderiza providers, contas com barra de cota e catálogo somente leitura', async () => {
    renderPage();

    // Nome do provider (h2) e o badge do kind usam o mesmo texto.
    expect(await screen.findByRole('heading', { level: 2, name: 'OpenAI' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2, name: 'Anthropic' })).toBeInTheDocument();
    expect(screen.getByText('Conta principal')).toBeInTheDocument();
    expect(screen.getByText('GPT-4o')).toBeInTheDocument();
    expect(screen.getByText('Claude Sonnet 4')).toBeInTheDocument();

    // Barras de cota acessíveis com o consumo das fixtures (87,35/150 = 58%).
    const bar = screen.getByRole('progressbar', { name: /Conta principal/ });
    expect(bar).toHaveAttribute('aria-valuenow', '58');

    // Política de roteamento em visualização estruturada.
    expect(screen.getByText('Política padrão')).toBeInTheDocument();
  });

  it('sincroniza o catálogo e confirma quantos modelos foram atualizados', async () => {
    const user = userEvent.setup();
    renderPage();

    const provider = await screen.findByRole('region', { name: 'OpenAI' });
    await user.click(within(provider).getByRole('button', { name: 'Sincronizar' }));

    expect(await within(provider).findByRole('status')).toHaveTextContent(
      /Catálogo sincronizado: \d+ modelo/,
    );
  });

  it('salva uma alteração estruturada na política de roteamento', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    await user.click(await screen.findByRole('button', { name: 'Editar política' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Editar política de roteamento',
    });
    const preferred = within(dialog).getAllByLabelText('Modelo preferido')[0];
    const target = bundle.fixtures.data.models.find(
      (model) => model.id !== (preferred as HTMLSelectElement).value,
    )!;

    await user.selectOptions(preferred, target.id);
    await user.click(within(dialog).getByRole('button', { name: 'Avançar' }));
    await user.click(within(dialog).getByRole('button', { name: 'Confirmar e salvar' }));

    await waitFor(async () => {
      const policies = (await bundle.api.list('routing-policies')).items;
      expect(policies[0].rules[0].preferredModelId).toBe(target.id);
    });
  });
});
