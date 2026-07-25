import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { createTestBundle } from '@/api/__tests__/test-utils';
import { ManagedAgentAvatar } from '@/features/shared/components/managed-agent-avatar';
import { canonicalPhotoAlias } from '@/features/shared/lib/agent-photo';
import { renderWithApi } from '@/test/render-with-providers';

describe('foto gerenciada de agentes', () => {
  it('unifica aliases de persona e conta na mesma foto', () => {
    expect(canonicalPhotoAlias('frontend-engineer')).toBe('worker-codex-frontend');
    expect(canonicalPhotoAlias('architecture-critic')).toBe('worker-antigravity-review');
    expect(canonicalPhotoAlias('prototype-designer')).toBe('worker-kimi-ui');
    expect(canonicalPhotoAlias('chief-orchestrator')).toBe('chief-claude-primary');
  });

  it('abre a foto ampliada pelo avatar e fecha com Esc', async () => {
    const user = userEvent.setup();
    renderWithApi(
      <ManagedAgentAvatar alias="worker-codex-frontend" size={44} />,
      createTestBundle(),
    );

    await user.click(screen.getByRole('button', { name: 'Ampliar foto de Aline Castro' }));
    expect(
      screen.getByRole('dialog', { name: 'Foto ampliada de Aline Castro' }),
    ).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(
      screen.queryByRole('dialog', { name: 'Foto ampliada de Aline Castro' }),
    ).not.toBeInTheDocument();
  });
});
