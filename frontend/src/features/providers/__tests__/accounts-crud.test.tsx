import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import ProvidersPage from '@/features/providers/pages/providers-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderPage() {
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={['/providers']}>
      <Routes>
        <Route path="/providers" element={<ProvidersPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

/** Cartão de uma conta na lista (article rotulado pelo apelido). */
function accountBlock(label: string): HTMLElement {
  return screen.getByRole('article', { name: label });
}

describe('ProvidersPage — CRUD de contas (FR-5)', () => {
  it('exibe identidade, plano, saúde e capacidades das contas, com fallback e badge Local', async () => {
    renderPage();

    expect(await screen.findByText('Conta principal')).toBeInTheDocument();
    expect(screen.getByText(/billing@poseidonlabs\.dev/)).toBeInTheDocument();
    expect(screen.getByText(/contas@poseidonlabs\.dev/)).toBeInTheDocument();
    expect(screen.getByText('Saudável')).toBeInTheDocument();
    expect(screen.getAllByText('Raciocínio').length).toBeGreaterThan(0);
    // Conta Ollama: e-mail null → fallback; plano preenchido.
    const ollama = accountBlock('Ollama deste computador');
    expect(within(ollama).getByText(/não informado/)).toBeInTheDocument();
    expect(within(ollama).getByText(/Local \(sem custo\)/)).toBeInTheDocument();
    // Badge "Local" derivado do provider (kind 'ollama'): conta + modelo.
    expect(within(ollama).getByText('Local')).toBeInTheDocument();
    const model = screen.getByRole('listitem', { name: 'Llama 3.1 8B (local)' });
    expect(within(model).getByText('Local')).toBeInTheDocument();
  });

  it('cria uma conta pelo formulário e a lista é atualizada', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    expect(await screen.findByText('Conta principal')).toBeInTheDocument();
    // Um CTA "Conectar conta" por provedor; o primeiro é o do cartão OpenAI.
    await user.click(screen.getAllByRole('button', { name: 'Conectar conta' })[0]);

    const dialog = await screen.findByRole('dialog', { name: 'Nova conta' });
    await user.type(within(dialog).getByLabelText(/Apelido/), 'Conta de testes');
    await user.type(
      within(dialog).getByLabelText(/Referência da credencial/),
      'keychain://poseidon/testes',
    );
    await user.type(within(dialog).getByLabelText(/E-mail\/identidade/), 'testes@poseidonlabs.dev');
    await user.selectOptions(within(dialog).getByLabelText('Plano'), 'free');
    await user.click(within(dialog).getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Conta de testes')).toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    const accounts = (await bundle.api.list('accounts')).items;
    const created = accounts.find((account) => account.label === 'Conta de testes')!;
    expect(created.identity).toBe('testes@poseidonlabs.dev');
    expect(created.plan).toBe('free');
    expect(created.state).toBe('disabled');
  });

  it('exige apelido para criar a conta', async () => {
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByText('Conta principal')).toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: 'Conectar conta' })[0]);

    const dialog = await screen.findByRole('dialog', { name: 'Nova conta' });
    await user.click(within(dialog).getByRole('button', { name: 'Salvar' }));

    expect(await within(dialog).findAllByRole('alert')).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ textContent: 'Informe um apelido para a conta.' }),
      ]),
    );
  });

  it('edita apelido e metadados operacionais sem trocar o provider', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    const block = await screen.findByText('Conta principal').then(() => accountBlock('Conta principal'));
    await user.click(within(block).getByRole('button', { name: 'Editar' }));

    const dialog = await screen.findByRole('dialog', { name: 'Editar conta' });
    // Provedor não pode ser trocado na edição.
    expect(within(dialog).getByLabelText('Provedor')).toBeDisabled();
    const labelInput = within(dialog).getByLabelText(/Apelido/);
    await user.clear(labelInput);
    await user.type(labelInput, 'Conta corporativa');
    await user.click(within(dialog).getByRole('button', { name: 'Salvar' }));

    expect(await screen.findByText('Conta corporativa')).toBeInTheDocument();
    const accounts = (await bundle.api.list('accounts')).items;
    expect(accounts.find((account) => account.label === 'Conta corporativa')).toBeDefined();
    expect(accounts.find((account) => account.label === 'Conta principal')).toBeUndefined();
  });

  it('habilita a conta Ollama (disabled → active)', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    const block = await screen
      .findByText('Ollama deste computador')
      .then(() => accountBlock('Ollama deste computador'));
    await user.click(within(block).getByRole('button', { name: 'Habilitar' }));

    await waitFor(() => {
      expect(within(accountBlock('Ollama deste computador')).getByText('Ativa')).toBeInTheDocument();
    });
    const accounts = (await bundle.api.list('accounts')).items;
    expect(accounts.find((account) => account.label === 'Ollama deste computador')!.state).toBe(
      'active',
    );
  });

  it('desabilita a conta principal (active → disabled)', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    const block = await screen.findByText('Conta principal').then(() => accountBlock('Conta principal'));
    await user.click(within(block).getByRole('button', { name: 'Desabilitar' }));

    await waitFor(() => {
      expect(within(accountBlock('Conta principal')).getByText('Desabilitada')).toBeInTheDocument();
    });
    const accounts = (await bundle.api.list('accounts')).items;
    expect(accounts.find((account) => account.label === 'Conta principal')!.state).toBe('disabled');
  });

  it('remove a conta Ollama (sem referências) após confirmação', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    const block = await screen
      .findByText('Ollama deste computador')
      .then(() => accountBlock('Ollama deste computador'));
    await user.click(within(block).getByRole('button', { name: 'Remover' }));

    const dialog = await screen.findByRole('dialog', { name: 'Remover conta' });
    await user.click(within(dialog).getByRole('button', { name: 'Remover conta' }));

    await waitFor(() => {
      expect(screen.queryByText('Ollama deste computador')).not.toBeInTheDocument();
    });
    const accounts = (await bundle.api.list('accounts')).items;
    expect(accounts.find((account) => account.label === 'Ollama deste computador')).toBeUndefined();
  });

  it('remoção de conta protegida (budget vinculado) exibe o erro 409 da API', async () => {
    const user = userEvent.setup();
    renderPage();

    // "Conta secundária" aparece na seção de contas e na de orçamentos — o
    // article rotulado é o cartão da conta.
    await screen.findByRole('article', { name: 'Conta secundária' });
    const block = accountBlock('Conta secundária');
    await user.click(within(block).getByRole('button', { name: 'Desabilitar' }));
    await waitFor(() => {
      expect(within(block).getByText('Desabilitada')).toBeInTheDocument();
    });
    await user.click(within(block).getByRole('button', { name: 'Remover' }));

    const dialog = await screen.findByRole('dialog', { name: 'Remover conta' });
    await user.click(within(dialog).getByRole('button', { name: 'Remover conta' }));

    // O dialog permanece aberto com o detalhe do problema retornado pela API.
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'A conta possui budget vinculado — remova o budget antes de excluir a conta.',
    );
    expect(screen.getAllByText('Conta secundária').length).toBeGreaterThan(0);
  });
});
