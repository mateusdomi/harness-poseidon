import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import SettingsPage from '@/features/settings/pages/settings-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderPage() {
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={['/settings']}>
      <Routes>
        <Route path="/settings" element={<SettingsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('SettingsPage', () => {
  it('renderiza seções com dados do mock (sandbox, diagnóstico, licença, sobre)', async () => {
    renderPage();

    expect(await screen.findByText('Preferências')).toBeInTheDocument();
    expect(screen.getByText('Diretórios de trabalho')).toBeInTheDocument();
    expect(screen.getByText('Sandbox e modo inseguro')).toBeInTheDocument();
    // Fixture: aceite do modo inseguro preenchido → data exibida + revogar.
    expect(screen.getByText(/Modo inseguro \(execução sem sandbox\) aceito em/)).toBeInTheDocument();
    expect(screen.getByText('Backup e restauração')).toBeInTheDocument();
    // Backup: orientação sobre o que é / onde fica.
    expect(screen.getByText(/arquivo local com o estado do dispositivo/)).toBeInTheDocument();
    expect(screen.getByText('Diagnóstico')).toBeInTheDocument();
    // Diagnóstico do mock: checks de api/realtime/licença/sandbox.
    expect((await screen.findAllByText('OK')).length).toBeGreaterThan(0);
    // Licença: rótulos explícitos (Situação + Plano), sem contradição.
    expect(screen.getByText('Situação')).toBeInTheDocument();
    expect(screen.getByText('Plano Pro')).toBeInTheDocument();
    // Diretório de trabalho: fixture com caminho → leitura do valor atual.
    expect(screen.getByText(/Diretório atual: ~\/poseidon/)).toBeInTheDocument();
    // Sobre: produto com codinome Harness.
    expect(screen.getByText(/codinome Harness/)).toBeInTheDocument();
  });

  it('revoga o aceite do modo inseguro com confirmação', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    await user.click(await screen.findByRole('button', { name: 'Revogar aceite' }));
    const dialog = await screen.findByRole('dialog', { name: 'Revogar aceite do modo inseguro' });
    await user.click(within(dialog).getByRole('button', { name: 'Revogar aceite' }));

    // Settings do perfil atual voltam a exigir aceite (unsafeModeAcceptedAt = null).
    await waitFor(async () => {
      const profile = await bundle.api.getCurrentProfile();
      const settings = (await bundle.api.list('settings', { filter: { profileId: profile.id } }))
        .items[0];
      expect(settings.unsafeModeAcceptedAt).toBeNull();
    });
    expect(
      await screen.findByText(/Modo inseguro não aceito/),
    ).toBeInTheDocument();
  });

  it('cria backup com confirmação e habilita o restore', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Criar backup' }));
    const dialog = await screen.findByRole('dialog', { name: 'Criar backup local' });
    await user.click(within(dialog).getByRole('button', { name: 'Criar backup' }));

    expect(await screen.findByText(/Backup criado em/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Restaurar último backup' })).toBeEnabled();
  });
});
