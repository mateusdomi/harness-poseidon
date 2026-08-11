import { screen, waitFor } from '@testing-library/react';

import { createTestBundle } from '@/api/__tests__/test-utils';
import { AgentExecutionRoster } from '@/features/agents/components/agent-execution-roster';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

const SEVEN_ALIASES = [
  'chief-claude-primary',
  'worker-claude-secondary',
  'worker-codex-frontend',
  'worker-codex-critic',
  'worker-antigravity-review',
  'worker-glm-general',
  'worker-kimi-ui',
];

describe('AgentExecutionRoster', () => {
  beforeEach(() => {
    usePresentationStore.setState({ modeByProfile: {} });
    useSessionStore.setState({ activeProfileId: null });
  });

  it('surfaces the 7 execution identities with provider and executor', async () => {
    const bundle = createTestBundle();
    const profileId = bundle.fixtures.meta.currentProfileId;
    useSessionStore.setState({ activeProfileId: profileId });
    usePresentationStore.getState().requestMode(profileId, 'technical');
    renderWithApi(<AgentExecutionRoster />, bundle);

    for (const alias of SEVEN_ALIASES) {
      expect(await screen.findByText(alias)).toBeInTheDocument();
    }
    // Providers e executores redigidos são exibidos.
    expect(screen.getAllByText('anthropic').length).toBeGreaterThan(0);
    expect(screen.getAllByText('antigravity').length).toBeGreaterThan(0);
  });

  it('presents people without infrastructure details in business mode', async () => {
    const { container } = renderWithApi(<AgentExecutionRoster />);

    expect(await screen.findByText('Equipe profissional disponível')).toBeInTheDocument();
    const text = container.textContent ?? '';
    expect(text).toContain('Equipe profissional disponível');
    expect(text).not.toContain('chief-claude-primary');
    expect(text).not.toContain('anthropic');
    expect(text).not.toContain('claude-code');
  });

  it('never renders any credential reference or secret value', async () => {
    // A cópia explicativa pode citar a palavra "token"/"credencial"; o que NÃO pode
    // aparecer é uma REFERÊNCIA de credencial (o vetor real de vazamento).
    const { container } = renderWithApi(<AgentExecutionRoster />);
    await screen.findByText('Equipe profissional disponível');

    const text = (container.textContent ?? '').toLowerCase();
    expect(text).not.toContain('keychain://');
    expect(text).not.toContain('confighome://');
    expect(text).not.toContain('://poseidon/');
    expect(text).not.toContain('secret://');
  });

  it('shows an error message when the roster fails to load', async () => {
    const bundle = createTestBundle();
    bundle.api.listAgentAccounts = () => Promise.reject(new Error('boom'));
    renderWithApi(<AgentExecutionRoster />, bundle);

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
    });
  });
});
