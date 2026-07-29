import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { vi } from 'vitest';

import { HeaderContext } from '@/app/components/header-context';
import { createTestBundle } from '@/api/__tests__/test-utils';
import { renderWithApi } from '@/test/render-with-providers';
import { useActiveProjectStore } from '@/stores/active-project-store';

describe('seletor global de projeto', () => {
  beforeEach(() => {
    useActiveProjectStore.setState({ selectionsByProfile: {} });
  });

  it('mostra carregamento e depois a seleção autorizada', async () => {
    const bundle = createTestBundle({ latency: { min: 40, max: 40 } });
    renderWithApi(<HeaderContext />, bundle);

    expect(screen.getByRole('status', { name: 'Carregando projetos' })).toBeInTheDocument();
    expect(await screen.findByRole('combobox', { name: 'Projeto ativo' })).toHaveValue(
      bundle.fixtures.data.projects[0].id,
    );
  });

  it('expõe erro com nova tentativa', async () => {
    const bundle = createTestBundle();
    const originalList = bundle.api.list.bind(bundle.api);
    vi.spyOn(bundle.api, 'list').mockImplementation((resource, query) => {
      if (resource === 'projects') return Promise.reject(new Error('offline'));
      return originalList(resource, query);
    });

    renderWithApi(<HeaderContext />, bundle);

    expect(
      await screen.findByRole('button', { name: /projetos indisponíveis/i }),
    ).toBeInTheDocument();
  });

  it('não substitui silenciosamente uma seleção inválida e permite corrigi-la', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    const profileId = bundle.fixtures.meta.currentProfileId;
    useActiveProjectStore.getState().selectProject(profileId, 'project-without-access');

    renderWithApi(<HeaderContext />, bundle);

    const selector = await screen.findByRole('combobox', { name: 'Projeto ativo' });
    expect(selector).toHaveValue('');
    expect(screen.getByRole('alert')).toHaveTextContent(
      /projeto selecionado não existe mais/i,
    );

    await user.selectOptions(selector, bundle.fixtures.data.projects[0].id);
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });
});

