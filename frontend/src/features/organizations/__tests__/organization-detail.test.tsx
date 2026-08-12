import { screen } from '@testing-library/react';

import { buildFixtures } from '@/api';
import { OrganizationDetail } from '@/features/organizations/components/organization-detail';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

const fixtures = buildFixtures(42);
const [orgPoseidon, orgPessoal] = fixtures.data.organizations;

function renderDetail(
  organization: typeof orgPoseidon,
  mode: 'business' | 'technical' = 'business',
) {
  useSessionStore.setState({ activeProfileId: fixtures.meta.currentProfileId });
  usePresentationStore.setState({ modeByProfile: {} });
  usePresentationStore.getState().requestMode(fixtures.meta.currentProfileId, mode);
  return renderWithApi(
    <OrganizationDetail organization={organization} onEdit={() => {}} onBack={() => {}} />,
  );
}

describe('OrganizationDetail', () => {
  it('mostra campos de marca preenchidos como personalizados', () => {
    renderDetail(orgPoseidon);

    // Poseidon Labs tem cor primária, secundária e tipografia definidas.
    expect(screen.getAllByText(/personalizado/i).length).toBe(3);
    expect(screen.getAllByText(/padrão do produto/i).length).toBe(1);
    expect(screen.getByText('#7C5CFC')).toBeInTheDocument();
    expect(screen.getByText('Space Grotesk')).toBeInTheDocument();
  });

  it('mostra campos de marca vazios como padrão do produto (herdados)', () => {
    renderDetail(orgPessoal);

    expect(screen.getAllByText(/padrão do produto/i).length).toBe(4);
    expect(screen.queryByText(/personalizado/i)).not.toBeInTheDocument();
  });

  it('lista políticas com estado e projetos associados', async () => {
    renderDetail(orgPoseidon, 'technical');

    expect(screen.getByText(/toda tarefa passa por revisão/i)).toBeInTheDocument();
    expect(screen.getAllByText('Ativa').length).toBeGreaterThan(0);
    expect(screen.getByText('Inativa')).toBeInTheDocument();

    // Projeto associado aparece após o carregamento do mock
    expect(await screen.findByText('Poseidon Frontend')).toBeInTheDocument();
    expect(screen.getAllByText('Ativo').length).toBeGreaterThanOrEqual(1);
  });

  it('mostra somente marca e projetos no modo Negócio', async () => {
    renderDetail(orgPoseidon);

    expect(screen.getByText('Marca')).toBeInTheDocument();
    expect(await screen.findByText('Projetos associados')).toBeInTheDocument();
    expect(screen.queryByText('Políticas')).not.toBeInTheDocument();
    expect(screen.queryByText('Templates')).not.toBeInTheDocument();
    expect(screen.queryByText('Fluxos de trabalho padrão')).not.toBeInTheDocument();
  });
});
