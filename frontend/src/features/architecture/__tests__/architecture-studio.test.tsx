import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithApi } from '@/test/render-with-providers';
import { ArchitectureApiProvider } from '../api/architecture-provider';
import { MockArchitectureApi } from '../api/architecture-api';
import { ArchitectureStudio } from '../pages/architecture-page';

function renderStudio() {
  const architecture = new MockArchitectureApi();
  return renderWithApi(
    <ArchitectureApiProvider client={architecture}>
      <ArchitectureStudio />
    </ArchitectureApiProvider>,
  );
}

describe('ArchitectureStudio (ARC-04)', () => {
  it('carrega o modelo e mostra o grafo na view "Modelo completo"', async () => {
    renderStudio();

    // Nó do sistema principal aparece no canvas (view padrão = modelo completo).
    expect(await screen.findByRole('button', { name: /Poseidon/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Frontend Web/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Painel de Observabilidade/ })).toBeInTheDocument();
  });

  it('troca de view (projeção) sobre o mesmo modelo — C4 Contexto filtra o grafo', async () => {
    const user = userEvent.setup();
    renderStudio();
    await screen.findByRole('button', { name: /Poseidon/ });

    await user.selectOptions(screen.getByLabelText('Projeção do modelo'), 'c4-context');

    // Contexto mantém sistemas/pessoas, remove containers.
    expect(screen.getByRole('button', { name: /Poseidon/ })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Cliente/ })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Frontend Web/ })).not.toBeInTheDocument();
  });

  it('seleciona um elemento e mostra propriedades no inspetor', async () => {
    const user = userEvent.setup();
    renderStudio();

    await user.click(await screen.findByRole('button', { name: /Gateway de Pagamento/ }));

    const inspector = screen.getByLabelText('Propriedades do elemento');
    expect(within(inspector).getByText(/Sistema externo de cobrança/)).toBeInTheDocument();
    // Propriedade "cost" do elemento aparece.
    expect(within(inspector).getByText('cost')).toBeInTheDocument();
  });

  it('bloqueia um elemento pelo inspetor (POST /elements/{id}/lock)', async () => {
    const user = userEvent.setup();
    renderStudio();

    await user.click(await screen.findByRole('button', { name: /Banco Principal/ }));
    const inspector = screen.getByLabelText('Propriedades do elemento');
    await user.click(within(inspector).getByRole('button', { name: /Bloquear elemento/ }));

    expect(
      await within(inspector).findByRole('button', { name: /Desbloquear elemento/ }),
    ).toBeInTheDocument();
  });

  it('adiciona um novo elemento ao modelo', async () => {
    const user = userEvent.setup();
    renderStudio();
    await screen.findByRole('button', { name: /Poseidon/ });

    await user.type(screen.getByLabelText('Nome'), 'Serviço de Teste');
    await user.type(screen.getByLabelText('Tipo'), 'container');
    await user.click(screen.getByRole('button', { name: 'Adicionar ao modelo' }));

    expect(await screen.findByRole('button', { name: /Serviço de Teste/ })).toBeInTheDocument();
  });

  it('lista e abre uma view salva vinda da API', async () => {
    const user = userEvent.setup();
    renderStudio();
    await screen.findByRole('button', { name: /Poseidon/ });

    // View salva "C4 — Contexto do Sistema" aparece na lista.
    const savedView = await screen.findByRole('button', { name: /Contexto do Sistema/ });
    await user.click(savedView);

    // Ao abrir, o canvas passa a projetar só os elementos da view salva.
    await waitFor(() =>
      expect(screen.queryByRole('button', { name: /Banco Principal/ })).not.toBeInTheDocument(),
    );
    expect(screen.getByRole('button', { name: /Poseidon/ })).toBeInTheDocument();
  });
});
