import type { ReactElement } from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';

import i18n from '@/i18n';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';
import { AppShell } from '@/app/app-shell';
import { NAV_GROUPS, NAV_ITEMS, navGroupsFor, isMultiProjectPath } from '@/app/navigation';
import { router } from '@/app/router';
import type { PresentationMode } from '@/app/presentation/presentation-policy';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';
import { useUiStore } from '@/stores/ui-store';
import { renderWithApi } from '@/test/render-with-providers';

// O redirecionamento real dispara `new Request()` do react-router, que conflita
// com o AbortSignal do jsdom no Node 26 (mesmo motivo de `require-profile`).
// Mockamos <Navigate> para provar o DESTINO do redirect, não o router.
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return {
    ...actual,
    Navigate: ({ to }: { to: string }) => <div data-testid="redirect">{`redirect:${to}`}</div>,
  };
});

function renderShell(mode: PresentationMode = 'business', bundle: TestBundle = createTestBundle()) {
  const profileId = bundle.fixtures.meta.currentProfileId;
  useSessionStore.setState({ activeProfileId: profileId });
  usePresentationStore.setState({ modeByProfile: {} });
  if (mode !== 'business') usePresentationStore.getState().requestMode(profileId, mode);

  const memoryRouter = createMemoryRouter(
    [
      {
        path: '/',
        Component: AppShell,
        children: [{ path: '*', element: <div>page content</div> }],
      },
    ],
    { initialEntries: ['/chat'] },
  );
  return renderWithApi(<RouterProvider router={memoryRouter} />, bundle);
}

/** Rótulos dos links da sidebar (a barra mobile repete alguns itens). */
function sidebarLinkNames(): string[] {
  const nav = screen.getAllByRole('navigation', { name: /navegação principal/i })[0];
  return within(nav)
    .getAllByRole('link')
    .map((link) => link.textContent?.trim() ?? '');
}

/** Espera a política de apresentação resolver (perfil + entitlements). */
async function waitForMenu(expected: string) {
  await waitFor(() => expect(sidebarLinkNames()).toContain(expected));
}

describe('navegação por modo de apresentação (F4)', () => {
  beforeEach(async () => {
    await i18n.changeLanguage('pt-BR');
    useUiStore.setState({ sidebarCollapsed: false, collapsedNavGroups: {}, mobileNavOpen: false });
    usePresentationStore.setState({ modeByProfile: {} });
  });

  it('Negócio lista exatamente a navegação homologada (D7), com Chat primeiro', async () => {
    renderShell();
    await waitForMenu('Chat');

    expect(sidebarLinkNames()).toEqual([
      'Chat',
      'Dashboard',
      'Quadro',
      'Conversas',
      'Projetos',
      'Central de Entregas',
      'Fluxos de trabalho',
      'Equipe',
      'Executar Projeto',
      'Documentos',
      'Protótipos',
      'Organizações',
      'Canais',
      'Licenças',
      'Notificações',
      'Configurações',
    ]);
  });

  it('Negócio não expõe nenhuma tela técnica nem administrativa', async () => {
    renderShell();
    await waitForMenu('Chat');

    const names = sidebarLinkNames();
    for (const hidden of [
      'Profissionais',
      'Governança',
      'Documentos de Governança',
      'Provedores',
      'Ferramentas',
      'Arquitetura',
      'Assistente de PO',
    ]) {
      expect(names).not.toContain(hidden);
    }
  });

  it('Técnico soma Profissionais, Governança, Documentos de Governança, Provedores e Ferramentas', async () => {
    renderShell('technical');
    await waitForMenu('Profissionais');

    const names = sidebarLinkNames();
    for (const added of ['Governança', 'Documentos de Governança', 'Provedores', 'Ferramentas']) {
      expect(names).toContain(added);
    }
    // Administrador ainda não: essas duas só aparecem no nível de cima.
    expect(names).not.toContain('Arquitetura');
    expect(names).not.toContain('Assistente de PO');
  });

  it('Administrador soma Arquitetura e Assistente de PO, mantendo o do Técnico', async () => {
    renderShell('admin');
    await waitForMenu('Arquitetura');

    const names = sidebarLinkNames();
    expect(names).toContain('Assistente de PO');
    expect(names).toContain('Profissionais');
  });

  it('a busca global indexa só o que o modo mostra', async () => {
    const user = userEvent.setup();
    renderShell();
    await waitForMenu('Chat');

    await user.click(screen.getByRole('button', { name: /buscar telas/i }));
    await user.type(screen.getByRole('combobox', { name: /busca global de telas/i }), 'governanca');

    expect(await screen.findByText(/nenhuma tela encontrada/i)).toBeInTheDocument();
  });

  it('Onboarding e Aprovações saíram do menu em todos os modos', () => {
    for (const mode of ['business', 'technical', 'admin'] as const) {
      const keys = navGroupsFor(mode)
        .flatMap((group) => group.items)
        .map((item) => item.key);
      expect(keys).not.toContain('onboarding');
      expect(keys).not.toContain('approvals');
    }
  });

  it('a escala de modos é cumulativa e o padrão é Negócio', () => {
    const business = navGroupsFor('business').flatMap((g) => g.items.map((i) => i.key));
    const technical = navGroupsFor('technical').flatMap((g) => g.items.map((i) => i.key));
    const admin = navGroupsFor('admin').flatMap((g) => g.items.map((i) => i.key));

    expect(technical).toEqual(expect.arrayContaining(business));
    expect(admin).toEqual(expect.arrayContaining(technical));
    expect(admin.length).toBe(NAV_ITEMS.length);
  });

  it('endereço antigo de Aprovações cai na aba de Documentos (D9)', () => {
    // Tabela de rotas real da aplicação, não uma cópia do teste.
    const shell = router.routes.find((route) => route.path === '/');
    const approvals = shell?.children?.find((route) => route.path === '/approvals');

    expect(approvals?.element).toBeDefined();
    render(approvals!.element as ReactElement);
    expect(screen.getByTestId('redirect')).toHaveTextContent('redirect:/documents?tab=approvals');
  });

  it('toda tela do registro tem rota, inclusive as escondidas do modo Negócio', () => {
    // Esconder do menu não pode virar rota inexistente: quem tem o endereço abre.
    const paths = NAV_ITEMS.map((item) => item.path);
    expect(paths).toContain('/agents');
    expect(paths).toContain('/architecture');
    expect(new Set(paths).size).toBe(paths.length);
  });

  it('todo item do registro aponta para a rota da sua chave', () => {
    for (const item of NAV_GROUPS.flatMap((group) => group.items)) {
      expect(item.path).toBe(`/${item.key}`);
    }
  });

  it('reconhece as telas multi-projeto que ignoram a seleção global', () => {
    expect(isMultiProjectPath('/projects')).toBe(true);
    expect(isMultiProjectPath('/delivery')).toBe(true);
    expect(isMultiProjectPath('/organizations')).toBe(true);
    expect(isMultiProjectPath('/organizations/01H')).toBe(true);
    expect(isMultiProjectPath('/chat')).toBe(false);
  });
});
