import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';

import i18n from '@/i18n';
import { AppShell } from '@/app/app-shell';
import { AppProviders } from '@/app/providers';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '@/app/router-future';

function renderShell(initialPath = '/') {
  const router = createMemoryRouter(
    [
      {
        path: '/',
        Component: AppShell,
        children: [
          { index: true, element: <div>page content</div> },
          { path: '*', element: <div>page content</div> },
        ],
      },
    ],
    { initialEntries: [initialPath], future: ROUTER_FUTURE_FLAGS },
  );
  return render(
    <AppProviders>
      <RouterProvider router={router} future={ROUTER_PROVIDER_FUTURE_FLAGS} />
    </AppProviders>,
  );
}

describe('AppShell', () => {
  // A instância i18n é global: garante pt-BR mesmo após o teste que troca o idioma.
  beforeEach(async () => {
    await i18n.changeLanguage('pt-BR');
  });

  it('renderiza a navegação principal e o conteúdo da rota', async () => {
    renderShell();

    expect(screen.getAllByRole('navigation', { name: /navegação principal/i }).length).toBeGreaterThan(0);
    expect(screen.getByRole('navigation', { name: /navegação inferior/i })).toBeInTheDocument();
    expect(await screen.findByText('page content')).toBeInTheDocument();
  });

  it('exibe o nome do produto e o badge de notificações', () => {
    renderShell();

    expect(screen.getAllByRole('img', { name: 'Poseidon' }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole('link', { name: /notificações/i }).length).toBeGreaterThan(0);
  });

  it('alterna o tema ao clicar no toggle', async () => {
    const user = userEvent.setup();
    renderShell();

    const initialTheme = document.documentElement.dataset.theme;
    const toggle = screen.getByRole('button', { name: /tema/i });

    await user.click(toggle);
    expect(document.documentElement.dataset.theme).not.toBe(initialTheme);

    await user.click(toggle);
    expect(document.documentElement.dataset.theme).toBe(initialTheme);
  });

  it('permite trocar o idioma para inglês', async () => {
    const user = userEvent.setup();
    renderShell();

    const select = screen.getByRole('combobox', { name: /idioma/i });
    await user.selectOptions(select, 'en');

    expect(await screen.findByRole('navigation', { name: /primary navigation/i })).toBeInTheDocument();
  });

  it('agrupa a navegação em seções rotuladas', () => {
    renderShell();

    expect(screen.getAllByText('Operação').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Orquestração').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Artefatos e governança').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Administração').length).toBeGreaterThan(0);
  });

  it('exibe o nome do projeto ativo no header', async () => {
    renderShell();

    // Fixture do mock: primeiro projeto é adotado como ativo.
    expect(await screen.findByText('Poseidon Frontend')).toBeInTheDocument();
  });

  it('mostra tooltip com o nome do item na sidebar colapsada', async () => {
    const user = userEvent.setup();
    renderShell();

    await user.click(screen.getByRole('button', { name: /recolher barra lateral/i }));
    // Sidebar colapsada: links só com ícone (aria-label); o texto visível some.
    await user.hover(screen.getAllByRole('link', { name: 'Cockpit' })[0]);

    expect(await screen.findByText('Cockpit')).toBeInTheDocument();
  });
});
