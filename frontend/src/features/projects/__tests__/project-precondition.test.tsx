import { screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import userEvent from '@testing-library/user-event';

import { ApiError, type Page, type ResourceKind, type ResourceMap } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import ProjectsPage from '@/features/projects/pages/projects-page';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useActiveProjectStore } from '@/stores/active-project-store';
import { useSessionStore } from '@/stores/session-store';

/**
 * GP-08: criar projeto exige organização. O formulário NÃO abre com um select de
 * organização vazio — a página mostra a pré-condição e a CTA para criar a
 * primeira organização, voltando ao fluxo de projeto com ela pré-selecionada.
 */
function renderWithoutOrganizations() {
  const bundle = createTestBundle();
  const realList = bundle.api.list.bind(bundle.api);
  vi.spyOn(bundle.api, 'list').mockImplementation(
    (resource: ResourceKind, query?: Parameters<typeof realList>[1]) => {
      if (resource === 'organizations' || resource === 'projects') {
        return Promise.resolve({ items: [], nextCursor: null } as Page<ResourceMap[ResourceKind]>);
      }
      return realList(resource, query);
    },
  );

  return renderWithApi(
    <MemoryRouter>
      <ProjectsPage />
    </MemoryRouter>,
    bundle,
  );
}

describe('ProjectsPage — pré-condição de organização (GP-08)', () => {
  it('mostra a pré-condição e a CTA em vez do formulário quando não há organização', async () => {
    renderWithoutOrganizations();

    expect(await screen.findByText(/crie uma organização primeiro/i)).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: /criar organização/i }),
    ).toBeInTheDocument();
  });

  it('não abre o formulário de projeto com um select de organização vazio', async () => {
    renderWithoutOrganizations();

    // Espera o estado assentar (sai do skeleton para a pré-condição).
    await screen.findByText(/crie uma organização primeiro/i);
    // Nenhum select é renderizado: nem o de organização do formulário, nem filtros.
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });

  it('mostra a falha de criação e preserva o formulário para correção', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    const profileId = bundle.fixtures.meta.currentProfileId;
    useSessionStore.setState({ activeProfileId: profileId });
    usePresentationStore.setState({ modeByProfile: {} });
    usePresentationStore.getState().requestMode(profileId, 'business');
    vi.spyOn(bundle.api, 'create').mockRejectedValueOnce(
      ApiError.of(409, 'project_already_exists', 'A project already exists.'),
    );
    renderWithApi(
      <MemoryRouter>
        <ProjectsPage />
      </MemoryRouter>,
      bundle,
    );

    await user.click(await screen.findByRole('button', { name: 'Novo projeto' }));
    await user.type(screen.getByLabelText(/título/i), 'Projeto duplicado');
    await user.type(screen.getByLabelText(/objetivo e contexto/i), 'Validar retorno de erro.');
    await user.click(screen.getByRole('button', { name: 'Criar projeto' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Já existe um projeto com este título ou esta sigla.',
    );
    expect(screen.getByLabelText(/título/i)).toHaveValue('Projeto duplicado');
  });

  it('torna o projeto recém-criado o contexto ativo', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    const profileId = bundle.fixtures.meta.currentProfileId;
    const created = bundle.fixtures.data.projects[1];
    useSessionStore.setState({ activeProfileId: profileId });
    useActiveProjectStore.setState({ selectionsByProfile: {} });
    vi.spyOn(bundle.api, 'create').mockResolvedValueOnce(created);

    renderWithApi(
      <MemoryRouter>
        <ProjectsPage />
      </MemoryRouter>,
      bundle,
    );

    await user.click(await screen.findByRole('button', { name: 'Novo projeto' }));
    await user.type(screen.getByLabelText(/título/i), 'Projeto novo');
    await user.type(screen.getByLabelText(/objetivo e contexto/i), 'Novo contexto isolado.');
    await user.click(screen.getByRole('button', { name: 'Criar projeto' }));

    await waitFor(() => {
      expect(useActiveProjectStore.getState().selectionsByProfile[profileId]?.projectId).toBe(
        created.id,
      );
    });
  });
});
