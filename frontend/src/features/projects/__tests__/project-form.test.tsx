import { screen, waitFor, within } from '@testing-library/react';
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
    await user.clear(screen.getByLabelText(/slug \(sigla\)/i));
    await user.type(screen.getByLabelText(/slug \(sigla\)/i), 'minuscula');
    await user.type(screen.getByLabelText(/descrição/i), 'Descrição do projeto.');
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    expect(
      await screen.findByText(/2 a 12 letras maiúsculas ou números/i),
    ).toBeInTheDocument();
  });

  it('gera a sigla automaticamente a partir do título e para ao ser editada', async () => {
    const user = userEvent.setup();
    renderForm();

    const key = screen.getByLabelText(/slug \(sigla\)/i);
    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    // Sem decisão manual no fluxo comum: a sigla vem do nome (§7).
    expect(key).toHaveValue('PROJETOTESTE');

    // Depois de editar manualmente, o nome não sobrescreve mais a sigla.
    await user.clear(key);
    await user.type(key, 'MANUAL');
    await user.type(screen.getByLabelText(/título/i), ' Extra');
    expect(key).toHaveValue('MANUAL');
  });

  it('pula para a aba com erro (Pessoas) quando as anteriores estão válidas', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderForm();

    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    await user.clear(screen.getByLabelText(/slug \(sigla\)/i));
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
    await user.clear(screen.getByLabelText(/slug \(sigla\)/i));
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

    // Rótulo exato = campo HEX (o seletor visual tem nome acessível próprio).
    await user.type(screen.getByLabelText('Cor primária'), '#123456');
    expect(screen.getAllByText(/personalizado/i).length).toBeGreaterThan(0);
  });

  it('sinaliza campos versionados nas abas Repositório, Tecnologias e Marca', () => {
    renderForm();

    // Um badge "Versionado" por aba versionada (todas montadas no DOM).
    expect(screen.getAllByText('Versionado')).toHaveLength(3);
  });
});

describe('ProjectForm — FR-4 (impacto e versionamento)', () => {
  const project = fixtures.data.projects[0]; // Poseidon: configVersion 3 + histórico

  function renderEditForm(onSubmit = vi.fn(), started = true) {
    return {
      onSubmit,
      ...renderWithApi(
        <ProjectForm
          organizations={organizations}
          initial={project}
          started={started}
          submitting={false}
          onSubmit={onSubmit}
          onCancel={() => {}}
        />,
      ),
    };
  }

  it('exibe a versão de config atual e o histórico de versões', () => {
    renderEditForm();

    expect(screen.getByText('Configuração v3')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { name: 'Histórico de configuração' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/Campos alterados: defaultBranch/)).toBeInTheDocument();
    expect(screen.getByText(/Campos alterados: technologies/)).toBeInTheDocument();
  });

  it('metadados (título) salvam sem cerimônia mesmo em projeto iniciado', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderEditForm();

    await user.type(screen.getByLabelText(/título/i), ' (rev)');
    await user.click(screen.getByRole('button', { name: 'Salvar' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(
      screen.queryByRole('dialog', { name: 'Impacto da alteração' }),
    ).not.toBeInTheDocument();
  });

  it('campo operacional em projeto iniciado abre o painel de impacto com confirmação', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderEditForm();

    await user.click(screen.getByRole('tab', { name: /repositório/i }));
    const branch = screen.getByLabelText(/branch padrão/i);
    await user.clear(branch);
    await user.type(branch, 'main');
    await user.click(screen.getByRole('button', { name: 'Salvar' }));

    // Painel de impacto ANTES de salvar; confirmação reforçada por checkbox.
    const dialog = await screen.findByRole('dialog', { name: 'Impacto da alteração' });
    expect(within(dialog).getByText(/execução de workflow em andamento/i)).toBeInTheDocument();
    expect(within(dialog).getByText('Branch padrão')).toBeInTheDocument();
    const confirm = within(dialog).getByRole('button', { name: 'Salvar mesmo assim' });
    expect(confirm).toBeDisabled();
    expect(onSubmit).not.toHaveBeenCalled();

    await user.click(within(dialog).getByRole('checkbox', { name: /Entendo que a execução ativa/i }));
    expect(confirm).toBeEnabled();
    await user.click(confirm);

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
  });

  it('campo operacional em projeto NÃO iniciado salva sem painel', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderEditForm(vi.fn(), false);

    await user.click(screen.getByRole('tab', { name: /repositório/i }));
    const branch = screen.getByLabelText(/branch padrão/i);
    await user.clear(branch);
    await user.type(branch, 'main');
    await user.click(screen.getByRole('button', { name: 'Salvar' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(
      screen.queryByRole('dialog', { name: 'Impacto da alteração' }),
    ).not.toBeInTheDocument();
  });
});
