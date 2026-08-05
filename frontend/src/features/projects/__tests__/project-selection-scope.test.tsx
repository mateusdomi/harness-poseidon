import { screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import userEvent from '@testing-library/user-event';

import { createTestBundle } from '@/api/__tests__/test-utils';
import ProjectsPage from '@/features/projects/pages/projects-page';
import { renderWithApi } from '@/test/render-with-providers';
import { useActiveProjectStore } from '@/stores/active-project-store';
import { useSessionStore } from '@/stores/session-store';

/**
 * Regressão do incidente observado ao vivo em 2026-08-05: o usuário clicava no
 * projeto Prisma na lista, seguia para o Chat, e a conversa aberta era a do
 * Poseidon — porque abrir um projeto NÃO o selecionava como contexto global.
 *
 * A regra que este teste fixa: abrir um projeto É selecioná-lo. Chat, Quadro e
 * anexos derivam do projeto ativo, e um clique que muda a tela sem mudar o
 * contexto mistura projetos em silêncio.
 */
describe('ProjectsPage — abrir um projeto seleciona o projeto (escopo de contexto)', () => {
  it('clicar num projeto da lista persiste a seleção do projeto ativo', async () => {
    const bundle = createTestBundle();
    const profile = await bundle.api.getCurrentProfile();
    useSessionStore.getState().setActiveProfile(profile.id);

    const projects = (await bundle.api.list('projects')).items;
    expect(projects.length).toBeGreaterThan(0);
    const target = projects[projects.length - 1];

    renderWithApi(
      <MemoryRouter>
        <ProjectsPage />
      </MemoryRouter>,
      bundle,
    );

    const card = await screen.findByRole('button', {
      name: new RegExp(target.name, 'i'),
    });
    await userEvent.click(card);

    await waitFor(() => {
      const selection = useActiveProjectStore.getState().selectionsByProfile[profile.id];
      expect(selection?.projectId).toBe(target.id);
    });
  });
});
