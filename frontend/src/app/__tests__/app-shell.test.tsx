import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';

import i18n from '@/i18n';
import { AppShell } from '@/app/app-shell';
import { AppProviders } from '@/app/providers';
import { useUiStore } from '@/stores/ui-store';

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
  // A instância i18n é global: garante pt-BR mesmo após o teste que troca o idioma.
  // O ui-store (zustand) é um singleton em memória: reseta a sidebar/grupos entre
  // testes para que o estado (recolher sidebar/grupos) não vaze de um teste ao outro.
  beforeEach(async () => {
    await i18n.changeLanguage('pt-BR');
    useUiStore.setState({ sidebarCollapsed: false, collapsedNavGroups: {}, mobileNavOpen: false });
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

  it('recolhe e expande um grupo de navegação, persistindo o estado', async () => {
    const user = userEvent.setup();
    renderShell();

    // "Conversas" só aparece na sidebar (não está na barra inferior mobile).
    expect(screen.getByRole('link', { name: 'Conversas' })).toBeInTheDocument();

    const groupHeader = screen.getByRole('button', { name: /recolher seção operação/i });
    await user.click(groupHeader);

    // Grupo recolhido: seus itens somem e o cabeçalho vira "Expandir".
    expect(screen.queryByRole('link', { name: 'Conversas' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: /expandir seção operação/i })).toBeInTheDocument();

    // Reabre para deixar o estado limpo (persistido em localStorage).
    await user.click(screen.getByRole('button', { name: /expandir seção operação/i }));
    expect(screen.getByRole('link', { name: 'Conversas' })).toBeInTheDocument();
  });

  it('exibe o perfil ativo logado no shell e abre o menu do perfil', async () => {
    const user = userEvent.setup();
    renderShell();

    // Fixture do mock: perfil ativo é "Mateus".
    expect(await screen.findByText('Mateus')).toBeInTheDocument();

    const trigger = (await screen.findAllByRole('button', { name: /perfil de mateus/i }))[0];
    await user.click(trigger);

    expect(await screen.findByText('Trocar de perfil')).toBeInTheDocument();
    expect(screen.getByText('Sair')).toBeInTheDocument();
  });
});
