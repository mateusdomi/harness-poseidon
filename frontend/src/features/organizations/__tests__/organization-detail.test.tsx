import { screen } from '@testing-library/react';

import { buildFixtures } from '@/api';
import { OrganizationDetail } from '@/features/organizations/components/organization-detail';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = buildFixtures(42);
const [orgPoseidon, orgPessoal] = fixtures.data.organizations;

describe('OrganizationDetail', () => {
  it('mostra campos de marca preenchidos como personalizados', () => {
    renderWithApi(
      <OrganizationDetail organization={orgPoseidon} onEdit={() => {}} onBack={() => {}} />,
    );

    // Poseidon Labs tem cor primária, secundária e tipografia definidas.
    expect(screen.getAllByText(/personalizado/i).length).toBe(3);
    expect(screen.getAllByText(/padrão do produto/i).length).toBe(1);
    expect(screen.getByText('#7C5CFC')).toBeInTheDocument();
    expect(screen.getByText('Space Grotesk')).toBeInTheDocument();
  });

  it('mostra campos de marca vazios como padrão do produto (herdados)', () => {
    renderWithApi(
      <OrganizationDetail organization={orgPessoal} onEdit={() => {}} onBack={() => {}} />,
    );

    expect(screen.getAllByText(/padrão do produto/i).length).toBe(4);
    expect(screen.queryByText(/personalizado/i)).not.toBeInTheDocument();
  });

  it('lista políticas com estado e projetos associados', async () => {
    renderWithApi(
      <OrganizationDetail organization={orgPoseidon} onEdit={() => {}} onBack={() => {}} />,
    );

    expect(screen.getByText(/toda tarefa passa por revisão/i)).toBeInTheDocument();
    expect(screen.getAllByText('Ativa').length).toBeGreaterThan(0);
    expect(screen.getByText('Inativa')).toBeInTheDocument();

    // Projeto associado aparece após o carregamento do mock
    expect(await screen.findByText('Poseidon Frontend')).toBeInTheDocument();
    expect(screen.getByText('Ativo')).toBeInTheDocument();
  });
});
