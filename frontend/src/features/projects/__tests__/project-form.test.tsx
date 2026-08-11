import { screen, waitFor, within } from '@testing-library/react';
import { QueryClientProvider } from '@tanstack/react-query';
import userEvent from '@testing-library/user-event';

import { buildFixtures } from '@/api';
import { ApiContext } from '@/app/api-context';
import { ProjectForm } from '@/features/projects/components/project-form';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = buildFixtures(42);
const organizations = fixtures.data.organizations;

beforeEach(() => {
  const profileId = fixtures.meta.currentProfileId;
  useSessionStore.setState({ activeProfileId: profileId });
  usePresentationStore.setState({ modeByProfile: {} });
  usePresentationStore.getState().requestMode(profileId, 'technical');
});

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
  it('mostra somente os quatro campos do modo Negócio e submete defaults automáticos', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    usePresentationStore.getState().requestMode(fixtures.meta.currentProfileId, 'business');
    renderForm(onSubmit);

    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.getByLabelText(/título/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/objetivo e contexto/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/prazo desejado/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/enviar arquivo de logo/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/sigla/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/provedor do repositório/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/workflow do projeto/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/selecione pelo menos uma pessoa/i)).not.toBeInTheDocument();

    const logo = new File([new Uint8Array([137, 80, 78, 71])], 'produto.png', {
      type: 'image/png',
    });
    await user.type(screen.getByLabelText(/título/i), 'Portal do Cliente');
    await user.type(
      screen.getByLabelText(/objetivo e contexto/i),
      'Permitir que clientes acompanhem seus pedidos.',
    );
    await user.type(screen.getByLabelText(/prazo desejado/i), '2026-09-30');
    await user.upload(screen.getByLabelText(/enviar arquivo de logo/i), logo);
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const [values, logoFile] = onSubmit.mock.calls[0];
    expect(values).toMatchObject({
      name: 'Portal do Cliente',
      key: 'PORTALDOCLIE',
      description: 'Permitir que clientes acompanhem seus pedidos.',
      targetDeadline: '2026-09-30',
      memberProfileIds: [],
      repositoryProvider: 'local',
      repositoryUrl: '',
      technologies: [],
    });
    expect(logoFile).toBe(logo);
  });

  it('permite criar projeto no modo Negócio só com título para continuar no Chat', async () => {
    const user = userEvent.setup();
    const onSubmit = vi.fn();
    usePresentationStore.getState().requestMode(fixtures.meta.currentProfileId, 'business');
    renderForm(onSubmit);

    await user.type(screen.getByLabelText(/título/i), 'Equipamentos');
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    const [values] = onSubmit.mock.calls[0];
    expect(values).toMatchObject({
      name: 'Equipamentos',
      key: 'EQUIPAMENTOS',
      description: '',
      repositoryProvider: 'local',
      repositoryUrl: '',
    });
  });

  it('exibe somente o painel da aba selecionada', async () => {
    const user = userEvent.setup();
    renderForm();
    const organization = document.getElementById('project-panel-organization');
    const identity = document.getElementById('project-panel-identity');

    expect(organization).not.toHaveAttribute('hidden');
    expect(organization).toHaveClass('flex');
    expect(identity).toHaveAttribute('hidden');
    expect(identity).toHaveClass('hidden');

    await user.click(screen.getByRole('tab', { name: /identidade/i }));
    expect(organization).toHaveAttribute('hidden');
    expect(organization).toHaveClass('hidden');
    expect(identity).not.toHaveAttribute('hidden');
    expect(identity).toHaveClass('flex');
  });

  it('valida a aba Identidade ao salvar e mostra o resumo de erros', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderForm();

    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    expect(await screen.findByText(/revise os campos destacados/i)).toBeInTheDocument();
    expect(await screen.findByText(/use pelo menos 2 caracteres/i)).toBeInTheDocument();
    // Organização já tem default; Identidade é a primeira seção com erro.
    expect(screen.getByRole('tab', { name: /identidade/i })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('valida a sigla (slug) em maiúsculas', async () => {
    const user = userEvent.setup();
    renderForm();

    await user.click(screen.getByRole('tab', { name: /identidade/i }));
    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    await user.clear(screen.getByLabelText(/sigla/i));
    await user.type(screen.getByLabelText(/sigla/i), 'minuscula');
    await user.click(screen.getByRole('tab', { name: /objetivo/i }));
    await user.type(screen.getByLabelText(/objetivo e contexto/i), 'Descrição do projeto.');
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    expect(await screen.findByText(/2 a 12 letras maiúsculas ou números/i)).toBeInTheDocument();
  });

  it('gera a sigla automaticamente a partir do título e para ao ser editada', async () => {
    const user = userEvent.setup();
    renderForm();

    await user.click(screen.getByRole('tab', { name: /identidade/i }));
    const key = screen.getByLabelText(/sigla/i);
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

    await user.click(screen.getByRole('tab', { name: /identidade/i }));
    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    await user.clear(screen.getByLabelText(/sigla/i));
    await user.type(screen.getByLabelText(/sigla/i), 'TESTE');
    await user.click(screen.getByRole('tab', { name: /objetivo/i }));
    await user.type(screen.getByLabelText(/objetivo e contexto/i), 'Descrição do projeto.');
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    expect(await screen.findByText(/selecione pelo menos uma pessoa/i)).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: /pessoas/i })).toHaveAttribute('aria-selected', 'true');
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('submete quando todas as abas são válidas', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderForm();

    await user.click(screen.getByRole('tab', { name: /identidade/i }));
    await user.type(screen.getByLabelText(/título/i), 'Projeto Teste');
    await user.clear(screen.getByLabelText(/sigla/i));
    await user.type(screen.getByLabelText(/sigla/i), 'TESTE');
    await user.click(screen.getByRole('tab', { name: /objetivo/i }));
    await user.type(screen.getByLabelText(/objetivo e contexto/i), 'Descrição do projeto.');

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

  it('pré-seleciona o workflow recomendado e permite alterar na criação', async () => {
    const user = userEvent.setup();
    renderWithApi(
      <ProjectForm
        organizations={organizations}
        workflowTemplates={fixtures.data['workflow-templates']}
        workflowVersions={fixtures.data['workflow-versions']}
        submitting={false}
        onSubmit={vi.fn()}
        onCancel={() => {}}
      />,
    );

    await user.click(screen.getByRole('tab', { name: /^workflow$/i }));
    const select = screen.getByLabelText(/workflow do projeto/i);
    expect(select).not.toHaveValue('');
    expect(screen.getByText('Recomendado')).toBeInTheDocument();
  });

  it('reconcilia o workflow recomendado quando o catálogo chega após o formulário', async () => {
    const user = userEvent.setup();
    const view = renderWithApi(
      <ProjectForm
        organizations={organizations}
        submitting={false}
        onSubmit={vi.fn()}
        onCancel={() => {}}
      />,
    );

    view.rerender(
      <ApiContext.Provider value={{ api: view.bundle.api, realtime: view.bundle.realtime }}>
        <QueryClientProvider client={view.queryClient}>
          <ProjectForm
            organizations={organizations}
            workflowTemplates={fixtures.data['workflow-templates']}
            workflowVersions={fixtures.data['workflow-versions']}
            submitting={false}
            onSubmit={vi.fn()}
            onCancel={() => {}}
          />
        </QueryClientProvider>
      </ApiContext.Provider>,
    );

    await user.click(screen.getByRole('tab', { name: /^workflow$/i }));
    await waitFor(() => expect(screen.getByLabelText(/workflow do projeto/i)).not.toHaveValue(''));
    expect(screen.getByText('Recomendado')).toBeInTheDocument();
  });

  it('mantém o arquivo de logo para upload ao salvar', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderForm();
    const file = new File([new Uint8Array([137, 80, 78, 71])], 'marca.png', {
      type: 'image/png',
    });

    await user.click(screen.getByRole('tab', { name: /marca/i }));
    await user.upload(screen.getByLabelText(/enviar arquivo de logo/i), file);
    await user.click(screen.getByRole('tab', { name: /identidade/i }));
    await user.type(screen.getByLabelText(/título/i), 'Projeto Logo');
    await user.clear(screen.getByLabelText(/sigla/i));
    await user.type(screen.getByLabelText(/sigla/i), 'LOGO');
    await user.click(screen.getByRole('tab', { name: /objetivo/i }));
    await user.type(screen.getByLabelText(/objetivo e contexto/i), 'Projeto com marca.');
    await user.click(screen.getByRole('tab', { name: /pessoas/i }));
    await user.click(await screen.findByLabelText(/Mateus/i));
    await user.click(screen.getByRole('button', { name: /criar projeto/i }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalled());
    expect(onSubmit.mock.calls[0][1]).toBe(file);
  });

  it('explica em um único lugar quais campos são versionados', () => {
    renderForm();

    expect(
      screen.getByText(/Repositório, tecnologias e marca são versionados/i),
    ).toBeInTheDocument();
    expect(screen.queryByText('Versionado')).not.toBeInTheDocument();
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
    expect(screen.getByRole('heading', { name: 'Histórico de configuração' })).toBeInTheDocument();
    expect(screen.getByText(/Campos alterados: defaultBranch/)).toBeInTheDocument();
    expect(screen.getByText(/Campos alterados: technologies/)).toBeInTheDocument();
  });

  it('não quebra quando o projeto vem sem configHistory (resposta real da API) — BUG-01', () => {
    // O backend `GET /api/v1/projects/{id}` não devolve `configHistory`; a tela
    // não pode ler `.length` de undefined (crash "Cannot read properties of undefined").
    const withoutHistory = { ...fixtures.data.projects[0] };
    delete (withoutHistory as { configHistory?: unknown }).configHistory;

    renderWithApi(
      <ProjectForm
        organizations={organizations}
        initial={withoutHistory}
        submitting={false}
        onSubmit={vi.fn()}
        onCancel={() => {}}
      />,
    );

    // Renderiza o cabeçalho de edição e OMITE a seção de histórico, sem lançar.
    expect(screen.getByText('Configuração v3')).toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Histórico de configuração' }),
    ).not.toBeInTheDocument();
  });

  it('metadados (título) salvam sem cerimônia mesmo em projeto iniciado', async () => {
    const user = userEvent.setup();
    const { onSubmit } = renderEditForm();

    await user.click(screen.getByRole('tab', { name: /identidade/i }));
    await user.type(screen.getByLabelText(/título/i), ' (rev)');
    await user.click(screen.getByRole('button', { name: 'Salvar' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole('dialog', { name: 'Impacto da alteração' })).not.toBeInTheDocument();
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
    expect(
      within(dialog).getByText(/execução de fluxo de trabalho em andamento/i),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('Branch padrão')).toBeInTheDocument();
    const confirm = within(dialog).getByRole('button', { name: 'Salvar mesmo assim' });
    expect(confirm).toBeDisabled();
    expect(onSubmit).not.toHaveBeenCalled();

    await user.click(
      within(dialog).getByRole('checkbox', { name: /Entendo que a execução ativa/i }),
    );
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
    expect(screen.queryByRole('dialog', { name: 'Impacto da alteração' })).not.toBeInTheDocument();
  });
});
