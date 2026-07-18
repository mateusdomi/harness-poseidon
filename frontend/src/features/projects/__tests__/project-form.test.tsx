import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { buildFixtures } from '@/api';
import { ProjectForm } from '@/features/projects/components/project-form';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = buildFixtures(42);
const organizations = fixtures.data.organizations;

function renderForm(onSubmit = vi.fn()) {
  return {
    onSubmit,
    ...renderWithApi(
      <ProjectForm
        organizations={organizations}
        submitting={false}
        onSubmit={onSubmit}
        onCancel={() => {}}
      />,
    ),
  };
}

describe('ProjectForm', () => {
  it('valida a aba Identificação ao salvar e mostra o resumo de erros', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderForm();

    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    expect(await screen.findByText(/revise os campos destacados/i)).toBeInTheDocument();
    expect(await screen.findByText(/use pelo menos 2 caracteres/i)).toBeInTheDocument();
    // A aba ativa continua sendo Identificação (primeira com erro)
    expect(screen.getByRole('tab', { name: /identificação/i })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('valida a sigla (slug) em maiúsculas', async () => {
    const user = userEvent.setup();
    renderForm();

    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    await user.type(screen.getByLabelText(/slug \(sigla\)/i), 'minuscula');
    await user.type(screen.getByLabelText(/descrição/i), 'Descrição do projeto.');
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    expect(
      await screen.findByText(/2 a 12 letras maiúsculas ou números/i),
    ).toBeInTheDocument();
  });

  it('pula para a aba com erro (Pessoas) quando as anteriores estão válidas', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderForm();

    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    await user.type(screen.getByLabelText(/slug \(sigla\)/i), 'TESTE');
    await user.type(screen.getByLabelText(/descrição/i), 'Descrição do projeto.');
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    expect(await screen.findByText(/selecione pelo menos uma pessoa/i)).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /pessoas/i })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('submete quando todas as abas são válidas', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderForm();

    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    await user.type(screen.getByLabelText(/slug \(sigla\)/i), 'TESTE');
    await user.type(screen.getByLabelText(/descrição/i), 'Descrição do projeto.');

    await user.click(screen.getByRole('tab', { name: /pessoas/i }));
    const firstMember = await screen.findByLabelText(/Mateus/i);
    await user.click(firstMember);

    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const values = onSubmit.mock.calls[0][0];
    expect(values.name).toBe('Projeto Teste');
    expect(values.key).toBe('TESTE');
    expect(values.memberProfileIds).toHaveLength(1);
  });

  it('mostra herança da marca da organização e marca sobrescrita ao preencher', async () => {
    const user = userEvent.setup();
    renderForm();

    await user.click(screen.getByRole('tab', { name: /marca/i }));

    // Organização padrão (Poseidon Labs) tem cor primária definida: campos
    // vazios herdam da organização.
    const inheritedBadges = await screen.findAllByText(/herdado da organização/i);
    expect(inheritedBadges.length).toBeGreaterThan(0);
    // Placeholder mostra o valor herdado
    expect(screen.getByPlaceholderText('#7C5CFC')).toBeInTheDocument();

    await user.type(screen.getByLabelText(/cor primária/i), '#123456');
    expect(screen.getAllByText(/personalizado/i).length).toBeGreaterThan(0);
  });

  it('sinaliza campos versionados nas abas Repositório, Tecnologias e Marca', () => {
    renderForm();

    // Um badge "Versionado" por aba versionada (todas montadas no DOM).
    expect(screen.getAllByText('Versionado')).toHaveLength(3);
  });
});
