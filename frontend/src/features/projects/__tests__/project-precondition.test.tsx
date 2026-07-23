import { screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

import type { Page, ResourceKind, ResourceMap } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import ProjectsPage from '@/features/projects/pages/projects-page';
import { renderWithApi } from '@/test/render-with-providers';

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
});
