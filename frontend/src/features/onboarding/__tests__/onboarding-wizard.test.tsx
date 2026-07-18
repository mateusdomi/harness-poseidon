import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { OnboardingWizard } from '@/features/onboarding/components/onboarding-wizard';
import { renderWithApi } from '@/test/render-with-providers';

async function advanceToSafetyStep(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/nome de exibição/i), 'Carla');
  await user.click(screen.getByRole('button', { name: /avançar/i }));
  // Preferências: defaults válidos
  await user.click(screen.getByRole('button', { name: /avançar/i }));
  // Diretório
  await user.type(screen.getByLabelText(/diretório de trabalho/i), '~/projetos');
  await user.click(screen.getByRole('button', { name: /avançar/i }));
}

describe('OnboardingWizard', () => {
  it('valida o nome antes de avançar da primeira etapa', async () => {
    const user = userEvent.setup();
    renderWithApi(<OnboardingWizard onCompleted={() => {}} />);

    await user.click(screen.getByRole('button', { name: /avançar/i }));

    expect(await screen.findByText(/use pelo menos 2 caracteres/i)).toBeInTheDocument();
    // Continua na etapa de perfil
    expect(screen.getByText(/etapa 1 de 4/i)).toBeInTheDocument();
  });

  it('exige o aceite explícito do modo inseguro para concluir', async () => {
    const user = userEvent.setup();
    const onCompleted = vi.fn();
    renderWithApi(<OnboardingWizard onCompleted={onCompleted} />);

    await advanceToSafetyStep(user);
    expect(screen.getByText(/etapa 4 de 4/i)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /concluir/i }));
    expect(
      await screen.findByText(/aceite explícito do modo inseguro é obrigatório/i),
    ).toBeInTheDocument();
    expect(onCompleted).not.toHaveBeenCalled();
  });

  it('conclui após o aceite, persistindo preferências e aceite no settings do mock', async () => {
    const user = userEvent.setup();
    const onCompleted = vi.fn();
    const { bundle } = renderWithApi(<OnboardingWizard onCompleted={onCompleted} />);

    await advanceToSafetyStep(user);
    await user.click(screen.getByLabelText(/entendo os riscos/i));
    await user.click(screen.getByRole('button', { name: /concluir/i }));

    await waitFor(() => expect(onCompleted).toHaveBeenCalledTimes(1));
    const profile = onCompleted.mock.calls[0][0];
    expect(profile.displayName).toBe('Carla');

    // Aceite e diretório persistidos no settings do perfil criado
    const settingsPage = await bundle.api.list('settings', {
      filter: { profileId: profile.id },
    });
    const settings = settingsPage.items[0];
    expect(settings).toBeDefined();
    expect(settings.unsafeModeAcceptedAt).not.toBeNull();
    expect(settings.workingDirectory).toBe('~/projetos');
  });
});
