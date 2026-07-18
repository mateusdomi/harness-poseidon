import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';

import '@/i18n';
import { AppShell } from '@/app/app-shell';
import { AppProviders } from '@/app/providers';

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
    { initialEntries: [initialPath] },
  );
  return render(
    <AppProviders>
      <RouterProvider router={router} />
    </AppProviders>,
  );
}

describe('AppShell', () => {
  it('renderiza a navegação principal e o conteúdo da rota', async () => {
    renderShell();

    expect(screen.getAllByRole('navigation', { name: /navegação principal/i }).length).toBeGreaterThan(0);
    expect(screen.getByRole('navigation', { name: /navegação inferior/i })).toBeInTheDocument();
    expect(await screen.findByText('page content')).toBeInTheDocument();
  });

  it('exibe o nome do produto e o badge de notificações', () => {
    renderShell();

    expect(screen.getAllByText('Poseidon').length).toBeGreaterThan(0);
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
});
