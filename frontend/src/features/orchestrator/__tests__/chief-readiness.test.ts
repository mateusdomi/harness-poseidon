import type { Account, Model } from '@/api';
import { deriveChiefReadiness, readinessAction } from '@/features/orchestrator/lib/orchestrator-derive';

const model = { id: 'model-1' } as unknown as Model;
const account = { id: 'account-1' } as unknown as Account;
const base = {
  model,
  account,
  hasWorkflow: true,
  agentState: 'idle' as const,
  isRunning: false,
  health: 'ok' as const,
};

describe('prontidão operacional da Bruna', () => {
  it('fica pronta quando modelo, conta, fluxo e autenticação estão satisfeitos', () => {
    expect(deriveChiefReadiness({ ...base, chiefAccountState: 'Available' })).toBe('ready');
    // Sinal de autenticação indisponível não bloqueia (compatibilidade com o contrato anterior).
    expect(deriveChiefReadiness(base)).toBe('ready');
  });

  it('não declara prontidão quando a conta runtime da Bruna exige autenticação', () => {
    const readiness = deriveChiefReadiness({ ...base, chiefAccountState: 'AuthenticationRequired' });
    expect(readiness).toBe('awaitingAuthentication');
    expect(readinessAction(readiness)).toBe('reviewAgents');
  });
});
