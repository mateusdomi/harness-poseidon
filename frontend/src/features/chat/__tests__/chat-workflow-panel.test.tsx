import { act, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useSearchParams } from 'react-router-dom';

import { buildFixtures, type Document, type Gate, type Phase } from '@/api';
import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';
import { WorkflowPanel } from '@/features/chat/components/workflow-panel';
import {
  documentHealth,
  documentsOfPhase,
  filterDocumentsByHealth,
  phaseProgress,
  phaseProgressEvidence,
} from '@/features/chat/lib/workflow-panel-derive';
import ChatPage from '@/features/chat/pages/chat-page';
import { renderWithApi } from '@/test/render-with-providers';
import { useUiStore } from '@/stores/ui-store';
import { usePresentationStore } from '@/stores/presentation-store';

const fixtures = buildFixtures(42);
const project = fixtures.data.projects[0];

function makeDoc(partial: Partial<Document>): Document {
  return {
    id: 'd1',
    projectId: 'p1',
    title: 'Doc',
    kind: 'note',
    state: 'planned',
    currentVersion: 1,
    classifications: [],
    phaseName: null,
    inconsistent: false,
    waiver: null,
    createdAt: '2026-07-01T00:00:00Z',
    updatedAt: '2026-07-01T00:00:00Z',
    ...partial,
  };
}

function makePhase(partial: Partial<Phase>): Phase {
  return {
    id: 'ph1',
    runId: 'r1',
    name: 'Fase',
    order: 1,
    state: 'pending',
    startedAt: null,
    finishedAt: null,
    progress: {
      completed: 0,
      total: 0,
      percent: 0,
      source: 'workflow_run_objectives_and_gates',
      updatedAt: null,
      tasks: { completed: 0, total: 0 },
      documents: { completed: 0, total: 0 },
      gates: { completed: 0, total: 0 },
    },
    deliverables: [],
    ...partial,
  };
}

function makeGate(partial: Partial<Gate>): Gate {
  return {
    id: 'g1',
    phaseId: 'ph1',
    runId: 'r1',
    name: 'Gate',
    state: 'pending',
    requiresApproval: true,
    decidedByProfileId: null,
    decidedAt: null,
    note: null,
    ...partial,
  };
}

describe('workflow-panel-derive', () => {
  it('mapeia os 8 estados + flag inconsistent para os conceitos documentais (D-068)', () => {
    expect(documentHealth(makeDoc({ state: 'planned' }))).toBe('planned');
    expect(documentHealth(makeDoc({ state: 'inElaboration' }))).toBe('inProduction');
    expect(documentHealth(makeDoc({ state: 'inReview' }))).toBe('produced');
    expect(documentHealth(makeDoc({ state: 'awaitingApproval' }))).toBe('inReview');
    expect(documentHealth(makeDoc({ state: 'approved' }))).toBe('approved');
    expect(documentHealth(makeDoc({ state: 'outdated' }))).toBe('rejected');
    expect(documentHealth(makeDoc({ state: 'superseded' }))).toBe('notApplicable');
    expect(documentHealth(makeDoc({ state: 'notApplicable' }))).toBe('notApplicable');
    // Flag inconsistent tem precedência sobre qualquer estado.
    expect(documentHealth(makeDoc({ state: 'inElaboration', inconsistent: true }))).toBe(
      'rejected',
    );
    expect(documentHealth(makeDoc({ state: 'approved', inconsistent: true }))).toBe('rejected');
  });

  it('filtra documentos por conceito e por fase (phaseName)', () => {
    const docs = [
      makeDoc({ id: 'a', phaseName: 'Fase', state: 'planned' }),
      makeDoc({ id: 'b', phaseName: 'Fase', state: 'approved' }),
      makeDoc({ id: 'c', phaseName: 'Outra', state: 'planned' }),
      makeDoc({ id: 'd', phaseName: null, state: 'planned' }),
    ];
    const phase = makePhase({ name: 'Fase' });
    expect(documentsOfPhase(docs, phase).map((d) => d.id)).toEqual(['a', 'b']);
    expect(filterDocumentsByHealth(docs, 'planned').map((d) => d.id)).toEqual(['a', 'c', 'd']);
    expect(filterDocumentsByHealth(docs, null)).toHaveLength(4);
  });

  it('usa o read model canônico do run, sem recalcular no cliente (D-069)', () => {
    const phase = makePhase({
      id: 'ph1',
      state: 'active',
      progress: {
        completed: 2,
        total: 4,
        percent: 50,
        source: 'workflow_run_objectives_and_gates',
        updatedAt: '2026-07-01T00:00:00Z',
        tasks: { completed: 0, total: 1 },
        documents: { completed: 1, total: 2 },
        gates: { completed: 1, total: 1 },
      },
    });
    const gates = [
      makeGate({ id: 'g1', state: 'approved' }),
      makeGate({ id: 'g2', state: 'pending' }),
    ];
    const docs = [
      makeDoc({ id: 'a', phaseName: 'Fase', state: 'approved' }),
      makeDoc({ id: 'b', phaseName: 'Fase', state: 'planned' }),
    ];
    // O servidor é a autoridade; listas locais divergentes não alteram o percentual.
    expect(phaseProgress(phase, gates, docs)).toBe(50);
    expect(phaseProgress(phase, [makeGate({ state: 'waived' })], [])).toBe(50);
  });

  it('explica o progresso pelos objetivos e gates duráveis do run', () => {
    const phase = makePhase({
      id: 'ph1',
      name: 'Fase',
      state: 'active',
      progress: {
        completed: 3,
        total: 3,
        percent: 100,
        source: 'workflow_run_objectives_and_gates',
        updatedAt: '2026-07-01T00:00:00Z',
        tasks: { completed: 1, total: 1 },
        documents: { completed: 1, total: 1 },
        gates: { completed: 1, total: 1 },
      },
    });
    const evidence = phaseProgressEvidence(
      phase,
      [makeGate({ state: 'approved' })],
      [makeDoc({ phaseName: 'Fase', state: 'approved' })],
      [
        {
          ...fixtures.data.tasks[0],
          phaseName: 'Fase',
          state: 'done',
        },
      ],
      [],
    );

    expect(evidence).toMatchObject({
      percent: 100,
      completed: 3,
      total: 3,
      phaseStateFallback: false,
    });
  });
});

function DocumentsMarker() {
  const [params] = useSearchParams();
  return <p>DOC {params.get('doc')}</p>;
}

function renderPanel(bundle: TestBundle = createTestBundle()) {
  const utils = renderWithApi(
    <MemoryRouter>
      <Routes>
        <Route path="/" element={<WorkflowPanel projectId={project.id} />} />
        <Route path="/documents" element={<DocumentsMarker />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
  return { ...utils, bundle };
}

describe('WorkflowPanel', () => {
  it('renderiza o lifecycle V3 em ordem sem expor fases V1', async () => {
    renderPanel();

    const lifecycle = await screen.findByRole('list', { name: 'Lifecycle V3 do projeto' });
    expect(within(lifecycle).getAllByRole('listitem').map((item) => item.textContent)).toEqual([
      expect.stringContaining('Entendimento'),
      expect.stringContaining('Desenvolvimento'),
      expect.stringContaining('Validação'),
      expect.stringContaining('Aceite Humano'),
    ]);
    expect(screen.queryByText('Triagem')).toBeNull();
    expect(screen.queryByText('Descoberta')).toBeNull();
    expect(screen.queryByText('Planejamento')).toBeNull();
    expect(screen.queryByText('Publicação')).toBeNull();
  });

  it('marca Desenvolvimento como etapa ativa quando lifecycle está BUILDING', async () => {
    const bundle = createTestBundle();
    const originalGetV3ProjectContext = bundle.api.getV3ProjectContext.bind(bundle.api);
    bundle.api.getV3ProjectContext = async (projectId) => ({
      ...(await originalGetV3ProjectContext(projectId)),
      currentLifecycleState: 'BUILDING',
    });
    renderPanel(bundle);

    const lifecycle = await screen.findByRole('list', { name: 'Lifecycle V3 do projeto' });
    expect(within(lifecycle).getByText('Entendimento').closest('li')?.textContent).toContain('✓');
    expect(within(lifecycle).getByText('Desenvolvimento').closest('li')?.textContent).toContain(
      '●',
    );
  });

  it('mostra pronto para homologação humana sem marcar homologado', async () => {
    const bundle = createTestBundle();
    const originalGetV3ProjectContext = bundle.api.getV3ProjectContext.bind(bundle.api);
    bundle.api.getV3ProjectContext = async (projectId) => ({
      ...(await originalGetV3ProjectContext(projectId)),
      currentLifecycleState: 'READY_FOR_HUMAN_ACCEPTANCE',
    });
    renderPanel(bundle);

    expect(await screen.findByText('Pronto para homologação humana')).toBeInTheDocument();
    expect(screen.queryByText('Homologado')).toBeNull();
  });

  it('usa o painel V3 também quando documentos antigos existem no projeto', async () => {
    renderPanel();

    expect(await screen.findByRole('list', { name: 'Lifecycle V3 do projeto' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Nota de arquitetura realtime/ })).toBeNull();
  });

  it('renderiza fallback legado quando contexto V3 não existe', async () => {
    const bundle = createTestBundle();
    bundle.api.getV3ProjectContext = async () => {
      throw new Error('v3_context_not_found');
    };
    renderPanel(bundle);

    expect(await screen.findByRole('button', { name: /Validação/ })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Nota de arquitetura realtime/ })).toBeInTheDocument();
  });
});

/* ---- Responsivo: aside no desktop (lg+) vs drawer no mobile ---- */

function stubMatchMedia(desktop: boolean) {
  window.matchMedia = ((query: string) => ({
    matches: desktop ? query === '(min-width: 1024px)' : false,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as unknown as typeof window.matchMedia;
}

afterEach(() => {
  // jsdom não tem matchMedia: remove o stub entre testes.
  delete (window as { matchMedia?: unknown }).matchMedia;
  act(() => {
    useUiStore.setState({ chatWorkflowPanelOpen: true });
    usePresentationStore.setState({ modeByProfile: {} });
  });
});

describe('ChatPage — painel de workflow responsivo', () => {
  function renderChat(bundle: TestBundle = createTestBundle()) {
    return renderWithApi(
      <MemoryRouter>
        <ChatPage />
      </MemoryRouter>,
      bundle,
    );
  }

  it('desktop (lg+): painel lateral persistente com toggle recolhível', async () => {
    stubMatchMedia(true);
    const user = userEvent.setup();
    renderChat();

    const aside = await screen.findByRole('complementary', {
      name: 'Acompanhamento do projeto',
    });
    expect(
      await within(aside).findByRole('list', { name: 'Lifecycle V3 do projeto' }),
    ).toBeInTheDocument();
    expect(within(aside).getByText('Entendimento')).toBeInTheDocument();
    expect(within(aside).getByText('Desenvolvimento')).toBeInTheDocument();
    expect(within(aside).queryByText(/gate/i)).not.toBeInTheDocument();

    // Recolhe e reabre pelo toggle (estado persistido na ui-store).
    await user.click(screen.getByRole('button', { name: 'Fechar acompanhamento do projeto' }));
    expect(
      screen.queryByRole('complementary', { name: 'Acompanhamento do projeto' }),
    ).toBeNull();
    expect(useUiStore.getState().chatWorkflowPanelOpen).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Abrir acompanhamento do projeto' }));
    expect(
      await screen.findByRole('complementary', { name: 'Acompanhamento do projeto' }),
    ).toBeInTheDocument();
    // Sem drawer no desktop.
    expect(screen.queryByRole('dialog', { name: 'Acompanhamento do projeto' })).toBeNull();
  });

  it('mobile (<lg): painel abre como drawer (dialog modal) e fecha com Esc', async () => {
    stubMatchMedia(false);
    const user = userEvent.setup();
    renderChat();

    // Chat não é sacrificado: nenhum aside; o botão abre o drawer.
    expect(screen.queryByRole('complementary')).toBeNull();
    await user.click(
      await screen.findByRole('button', { name: 'Abrir acompanhamento do projeto' }),
    );

    const drawer = await screen.findByRole('dialog', { name: 'Acompanhamento do projeto' });
    expect(drawer).toHaveAttribute('aria-modal', 'true');
    expect(
      await within(drawer).findByRole('list', { name: 'Lifecycle V3 do projeto' }),
    ).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Acompanhamento do projeto' })).toBeNull();
  });

  it('modo técnico também preserva o lifecycle V3 no painel', async () => {
    stubMatchMedia(true);
    const bundle = createTestBundle();
    usePresentationStore
      .getState()
      .requestMode(bundle.fixtures.meta.currentProfileId, 'technical');
    renderChat(bundle);

    const aside = await screen.findByRole('complementary', { name: 'Workflow do projeto' });
    expect(
      await within(aside).findByRole('list', { name: 'Lifecycle V3 do projeto' }),
    ).toBeInTheDocument();
    expect(within(aside).getByText('Validação')).toBeInTheDocument();
  });
});
