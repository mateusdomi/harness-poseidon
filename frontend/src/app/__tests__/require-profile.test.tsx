import { screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import '@/i18n';
import { ApiError } from '@/api';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';
import { RequireProfile } from '@/app/components/require-profile';
import { useSessionStore } from '@/stores/session-store';
import { renderWithApi } from '@/test/render-with-providers';

// O redirecionamento real dispara `new Request()` do react-router, que
// conflita com o AbortSignal do jsdom no Node 26. Mockamos <Navigate> para
// testar a DECISÃO do guard (renderiza redirect vs. conteúdo), não o router.
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return {
    ...actual,
    Navigate: ({ to }: { to: string }) => <div data-testid="redirect">{`redirect:${to}`}</div>,
  };
});

function renderGuard(bundle: TestBundle = createTestBundle()) {
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/projetos']}>
      <RequireProfile>
        <div>conteúdo protegido</div>
      </RequireProfile>
    </MemoryRouter>,
    bundle,
  );
}

describe('RequireProfile (gate de onboarding)', () => {
  beforeEach(() => {
    useSessionStore.setState({ activeProfileId: null });
  });

  it('sem perfil ativo, redireciona para /onboarding', () => {
    renderGuard();

    expect(screen.getByTestId('redirect')).toHaveTextContent('redirect:/onboarding');
    expect(screen.queryByText('conteúdo protegido')).not.toBeInTheDocument();
  });

  it('com perfil ativo e cookie válido, renderiza o conteúdo protegido', async () => {
    const bundle = createTestBundle();
    useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
    renderGuard(bundle);

    expect(await screen.findByText('conteúdo protegido')).toBeInTheDocument();
    expect(screen.queryByTestId('redirect')).not.toBeInTheDocument();
  });

  it('perfil persistido inexistente recupera para onboarding', async () => {
    const bundle = createTestBundle();
    vi.spyOn(bundle.api, 'getCurrentProfile').mockRejectedValue(
      ApiError.of(404, 'profile_not_found'),
    );
    useSessionStore.setState({ activeProfileId: '01ARZ3NDEKTSV4RRFFQ69G5FAV' });
    renderGuard(bundle);

    expect(await screen.findByTestId('redirect')).toHaveTextContent('redirect:/onboarding');
  });
});
