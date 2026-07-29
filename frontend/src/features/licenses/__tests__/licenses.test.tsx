import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import LicensesPage from '@/features/licenses/pages/licenses-page';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

function renderPage(mode: 'business' | 'technical' = 'business') {
  const bundle = createTestBundle();
  useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
  usePresentationStore.setState({ modeByProfile: {} });
  usePresentationStore.getState().requestMode(bundle.fixtures.meta.currentProfileId, mode);
  return renderWithApi(
    <MemoryRouter initialEntries={['/licenses']}>
      <Routes>
        <Route path="/licenses" element={<LicensesPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('LicensesPage', () => {
  it('resume a licença e os recursos em linguagem de negócio', async () => {
    renderPage();

    expect(await screen.findByText('Estado da licença')).toBeInTheDocument();
    expect(screen.getByText('Ativa')).toBeInTheDocument();
    expect(screen.getByText('Pro')).toBeInTheDocument();
    expect(screen.getByText(/leitura e a exportação dos seus dados continuam/)).toBeInTheDocument();
    expect(screen.getByText('Projetos ativos ao mesmo tempo')).toBeInTheDocument();
    expect(screen.getByText('Acesso com conta corporativa')).toBeInTheDocument();
    expect(screen.queryByText('projects.max')).not.toBeInTheDocument();
    expect(screen.queryByText('sso.oidc')).not.toBeInTheDocument();
    expect(screen.queryByText(/01J/)).not.toBeInTheDocument();
    expect(screen.queryByText('Modo offline')).not.toBeInTheDocument();
  });

  it('mostra identificadores e estado do dispositivo somente no modo Técnico', async () => {
    renderPage('technical');

    expect(await screen.findByText('projects.max')).toBeInTheDocument();
    expect(screen.getByText('sso.oidc')).toBeInTheDocument();
    expect(screen.getByText('Dispositivo')).toBeInTheDocument();
    expect(screen.getByText('Modo offline')).toBeInTheDocument();
  });

  it('rejeita chave fora do formato e ativa com chave válida', async () => {
    const user = userEvent.setup();
    renderPage();

    const input = await screen.findByLabelText('Chave da licença');

    await user.type(input, 'chave-invalida');
    await user.click(screen.getByRole('button', { name: 'Ativar licença' }));
    expect(
      await screen.findByText('A chave deve ter o formato XXXX-XXXX-XXXX-XXXX.'),
    ).toBeInTheDocument();

    await user.clear(input);
    await user.type(input, 'AB12-CD34-EF56-GH78');
    await user.click(screen.getByRole('button', { name: 'Ativar licença' }));
    expect(await screen.findByText('Licença ativada com sucesso.')).toBeInTheDocument();
  });
});
