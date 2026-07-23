import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import LicensesPage from '@/features/licenses/pages/licenses-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderPage() {
  const bundle = createTestBundle();
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
  it('renderiza estado da licença, aviso pós-expiração e entitlements', async () => {
    renderPage();

    expect(await screen.findByText('Estado da licença')).toBeInTheDocument();
    expect(screen.getByText('Ativa')).toBeInTheDocument();
    expect(screen.getByText('Pro')).toBeInTheDocument();
    expect(screen.getByText(/leitura e a exportação dos seus dados continuam/)).toBeInTheDocument();
    // 5 entitlements da fixture.
    expect(screen.getByText('projects.max')).toBeInTheDocument();
    expect(screen.getByText('sso.oidc')).toBeInTheDocument();
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
