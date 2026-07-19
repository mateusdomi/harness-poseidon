import { screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  BRIEFING_TAG_PREFIX,
  FLOW_MOMENT_TAG_PREFIX,
  buildReferenceTags,
  effectiveBrand,
  plainTags,
  prefixedTagValue,
} from '@/features/prototypes/lib/prototypes-derive';
import PrototypesPage from '@/features/prototypes/pages/prototypes-page';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = createTestBundle().fixtures.data;
const projetoPoseidon = fixtures.projects[0];
const orgPoseidon = fixtures.organizations.find((o) => o.id === projetoPoseidon.organizationId)!;

function renderPage() {
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/prototypes']}>
      <Routes>
        <Route path="/prototypes" element={<PrototypesPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('prototypes-derive', () => {
  it('monta tags com metadados prefixados e ignora vazios', () => {
    const tags = buildReferenceTags({
      tags: 'tema, dashboard, ',
      briefing: 'Visual denso para telemetria',
      flowMoment: '',
    });
    expect(tags).toEqual(['tema', 'dashboard', `${BRIEFING_TAG_PREFIX}Visual denso para telemetria`]);
  });

  it('lê metadados prefixados e separa as tags visíveis', () => {
    const tags = ['tema', `${BRIEFING_TAG_PREFIX}Brief`, `${FLOW_MOMENT_TAG_PREFIX}Checkout`];
    expect(prefixedTagValue(tags, BRIEFING_TAG_PREFIX)).toBe('Brief');
    expect(prefixedTagValue(tags, FLOW_MOMENT_TAG_PREFIX)).toBe('Checkout');
    expect(prefixedTagValue(tags, 'outro:')).toBeNull();
    expect(plainTags(tags)).toEqual(['tema']);
  });

  it('marca efetiva herda campo a campo da organização', () => {
    const { brand, inheritedFields } = effectiveBrand(projetoPoseidon, orgPoseidon);
    // O projeto Poseidon tem brand toda nula → herda as cores da organização.
    expect(brand.primaryColor).toBe(orgPoseidon.brand.primaryColor);
    expect(inheritedFields).toContain('primaryColor');

    const custom = {
      ...projetoPoseidon,
      brand: { ...projetoPoseidon.brand, primaryColor: '#123456' },
    };
    const resultado = effectiveBrand(custom, orgPoseidon);
    expect(resultado.brand.primaryColor).toBe('#123456');
    expect(resultado.inheritedFields).not.toContain('primaryColor');
  });
});

describe('PrototypesPage', () => {
  it('renderiza cenário, protótipos e referências do projeto ativo', async () => {
    renderPage();

    expect(await screen.findByText('Geração autônoma')).toBeInTheDocument();
    // Fixtures: 3 protótipos e 4 referências do projeto Poseidon.
    expect(await screen.findByText('Cockpit v1')).toBeInTheDocument();
    expect(screen.getByText('Board v2')).toBeInTheDocument();
    expect(screen.getByText('Referência de kanban denso')).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 2, name: 'Protótipos' }),
    ).toBeInTheDocument();
    expect(
      screen.getByRole('heading', { level: 2, name: 'Referências visuais' }),
    ).toBeInTheDocument();
  });
});
