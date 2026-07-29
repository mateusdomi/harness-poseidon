import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import SettingsPage from '@/features/settings/pages/settings-page';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

function renderPage(mode: 'business' | 'technical' = 'business') {
  const bundle = createTestBundle();
  useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
  usePresentationStore.setState({ modeByProfile: {} });
  usePresentationStore.getState().requestMode(bundle.fixtures.meta.currentProfileId, mode);
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
  it('mantém o modo Negócio focado em preferências, backup e licença', async () => {
    renderPage();

    expect(await screen.findByText('Preferências')).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Modo de apresentação' })).toHaveValue('business');
    expect(
      within(screen.getByRole('combobox', { name: 'Modo de apresentação' })).getByRole('option', {
        name: 'Administrador',
      }),
    ).toBeInTheDocument();
    expect(screen.getByText('Backup e restauração')).toBeInTheDocument();
    expect(screen.getByText(/cópia de segurança do seu trabalho/)).toBeInTheDocument();
    expect(screen.queryByText('Diretórios de trabalho')).not.toBeInTheDocument();
    expect(screen.queryByText('Proteção de isolamento e modo inseguro')).not.toBeInTheDocument();
    expect(screen.queryByText('Diagnóstico')).not.toBeInTheDocument();
    expect(screen.getByText('Situação')).toBeInTheDocument();
    expect(screen.getByText('Plano Pro')).toBeInTheDocument();
    expect(screen.getByText(/codinome Harness/)).toBeInTheDocument();
  });

  it('exibe diretório, isolamento e diagnóstico no modo Técnico', async () => {
    renderPage('technical');

    expect(await screen.findByText('Diretórios de trabalho')).toBeInTheDocument();
    expect(screen.getByText('Proteção de isolamento e modo inseguro')).toBeInTheDocument();
    expect(
      screen.getByText(/Modo inseguro \(execução sem proteção de isolamento\) aceito em/),
    ).toBeInTheDocument();
    expect(screen.getByText('Diagnóstico')).toBeInTheDocument();
    expect((await screen.findAllByText('OK')).length).toBeGreaterThan(0);
    expect(screen.getByText(/Diretório atual: ~\/poseidon/)).toBeInTheDocument();
  });

  it('revoga o aceite do modo inseguro com confirmação', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage('technical');

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
    expect(await screen.findByText(/Modo inseguro não aceito/)).toBeInTheDocument();
  });

  it('cria backup com confirmação e habilita o restore', async () => {
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: 'Criar backup' }));
    const dialog = await screen.findByRole('dialog', { name: 'Criar backup local' });
    await user.click(within(dialog).getByRole('button', { name: 'Criar backup' }));

    expect(await screen.findByText(/Cópia de segurança criada em/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Restaurar último backup' })).toBeEnabled();
  });
});
