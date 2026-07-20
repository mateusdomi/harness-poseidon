import { screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';

import { createTestBundle } from '@/api/__tests__/test-utils';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';
import { GovernanceHealthCard } from '@/features/cockpit/components/governance-health-card';
import { renderWithApi } from '@/test/render-with-providers';

it('resume receipts reais do projeto sem inferir findings globais', async () => {
  const bundle = createTestBundle();
  const list = vi.spyOn(bundle.api, 'listGovernanceReceipts').mockResolvedValue([{
    projectId: 'project-1', taskId: 'task-1', attemptId: 'attempt-1', turnId: 'turn-1', agentId: 'agent-1',
    manifestVersion: '1', documents: [], estimatedTokens: 10, actualPromptTokens: 10,
    truncated: ['doc-large'], conflicts: ['canonical-conflict'], cacheHits: 0, provider: 'claude', model: null,
    timestamp: '2026-07-20T12:00:00Z', bundleChecksum: 'sha256:bundle', state: 'completed', gateResult: 'failed', version: 1,
  }]);

  renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS}>
      <GovernanceHealthCard projectId="project-1" />
    </MemoryRouter>,
    bundle,
  );

  expect(await screen.findByText('Saúde da governança')).toBeInTheDocument();
  expect(screen.getByText('atenção')).toBeInTheDocument();
  expect(screen.getByRole('link', { name: 'Abrir governança' })).toHaveAttribute('href', '/governance');
  expect(list).toHaveBeenCalledWith({ projectId: 'project-1', limit: 25 });
});
