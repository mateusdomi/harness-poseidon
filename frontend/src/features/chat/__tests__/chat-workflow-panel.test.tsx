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
} from '@/features/chat/lib/workflow-panel-derive';
import ChatPage from '@/features/chat/pages/chat-page';
import { renderWithApi } from '@/test/render-with-providers';
import { useUiStore } from '@/stores/ui-store';

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
    expect(documentHealth(makeDoc({ state: 'planned' }))).toBe('notProduced');
    expect(documentHealth(makeDoc({ state: 'inElaboration' }))).toBe('produced');
    expect(documentHealth(makeDoc({ state: 'inReview' }))).toBe('produced');
    expect(documentHealth(makeDoc({ state: 'awaitingApproval' }))).toBe('awaitingApproval');
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
    expect(filterDocumentsByHealth(docs, 'notProduced').map((d) => d.id)).toEqual(['a', 'c', 'd']);
    expect(filterDocumentsByHealth(docs, null)).toHaveLength(4);
  });

  it('deriva o progresso da fase de gates + documentos, sem inventar valores (D-069)', () => {
    const phase = makePhase({ id: 'ph1', state: 'active' });
    const gates = [
      makeGate({ id: 'g1', state: 'approved' }),
      makeGate({ id: 'g2', state: 'pending' }),
    ];
    const docs = [
      makeDoc({ id: 'a', phaseName: 'Fase', state: 'approved' }),
      makeDoc({ id: 'b', phaseName: 'Fase', state: 'planned' }),
    ];
    // 2 concluídos (1 gate + 1 doc) de 4 itens = 50%.
    expect(phaseProgress(phase, gates, docs)).toBe(50);
    // Gate dispensado conta como concluído.
    expect(phaseProgress(phase, [makeGate({ state: 'waived' })], [])).toBe(100);
    // Sem itens: fallback honesto pelo estado.
    expect(phaseProgress(makePhase({ state: 'completed' }), [], [])).toBe(100);
    expect(phaseProgress(makePhase({ state: 'skipped' }), [], [])).toBe(100);
    expect(phaseProgress(makePhase({ state: 'active' }), [], [])).toBe(0);
    expect(phaseProgress(makePhase({ state: 'pending' }), [], [])).toBe(0);
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
  it('renderiza as fases em ordem, com badge de estado e barra de progresso', async () => {
    renderPanel();

    const validacao = await screen.findByRole('button', { name: /Validação/ });
    const headers = screen
      .getAllByRole('button')
      .filter((button) => button.hasAttribute('aria-expanded'));
    expect(headers.map((button) => button.textContent)).toEqual([
      expect.stringContaining('Planejamento'),
      expect.stringContaining('Execução'),
      expect.stringContaining('Validação'),
      expect.stringContaining('Publicação'),
    ]);

    // Fase ativa abre por padrão; demais fechadas.
    expect(validacao).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('button', { name: /Planejamento/ })).toHaveAttribute(
      'aria-expanded',
      'false',
    );

    // Uma barra 0–100 por fase, com nome acessível.
    expect(screen.getAllByRole('progressbar')).toHaveLength(4);
    expect(
      screen.getByRole('progressbar', { name: 'Progresso da fase Validação' }),
    ).toHaveAttribute('aria-valuenow', '0');

    // Documento da fase ativa com estado como TEXTO (não só cor).
    expect(screen.getByRole('link', { name: /Nota de arquitetura realtime/ })).toBeInTheDocument();
    expect(screen.getByText('Aguardando aprovação')).toBeInTheDocument();
  });

  it('acordeão acessível: Enter abre/fecha e setas movem o foco entre fases', async () => {
    const user = userEvent.setup();
    renderPanel();

    const planejamento = await screen.findByRole('button', { name: /Planejamento/ });
    await user.click(planejamento);
    expect(planejamento).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('link', { name: /PRD do Poseidon Console/ })).toBeInTheDocument();

    // Setas: ↑ da primeira fase dá a volta para a última; ↓ avança.
    planejamento.focus();
    await user.keyboard('{ArrowUp}');
    expect(screen.getByRole('button', { name: /Publicação/ })).toHaveFocus();
    await user.keyboard('{ArrowDown}');
    expect(planejamento).toHaveFocus();

    // Enter (click de teclado) fecha.
    await user.keyboard('{Enter}');
    expect(planejamento).toHaveAttribute('aria-expanded', 'false');
  });

  it('filtra os documentos da fase pelos chips de conceito documental', async () => {
    const user = userEvent.setup();
    renderPanel();

    const header = await screen.findByRole('button', { name: /Planejamento/ });
    await user.click(header);
    // Chips escopados na seção da fase (outras fases têm os mesmos rótulos).
    const section = header.closest('section')!;
    expect(within(section).getByRole('link', { name: /PRD do Poseidon Console/ })).toBeInTheDocument();
    expect(within(section).getByRole('link', { name: /Spec da API v1/ })).toBeInTheDocument();

    await user.click(within(section).getByRole('button', { name: 'Aguardando aprovação (1)' }));
    expect(within(section).queryByRole('link', { name: /PRD do Poseidon Console/ })).toBeNull();
    expect(within(section).getByRole('link', { name: /Spec da API v1/ })).toBeInTheDocument();

    // Clicar de novo limpa o filtro.
    await user.click(within(section).getByRole('button', { name: 'Aguardando aprovação (1)' }));
    expect(within(section).getByRole('link', { name: /PRD do Poseidon Console/ })).toBeInTheDocument();
  });

  it('clique no documento navega para /documents?doc=<id>', async () => {
    const user = userEvent.setup();
    renderPanel();

    const nota = fixtures.data.documents.find((d) => d.title === 'Nota de arquitetura realtime')!;
    await user.click(await screen.findByRole('link', { name: /Nota de arquitetura realtime/ }));
    expect(await screen.findByText(`DOC ${nota.id}`)).toBeInTheDocument();
  });

  it('atualiza o estado do documento ao receber document.stateChanged do stream', async () => {
    const { bundle } = renderPanel();

    const nota = fixtures.data.documents.find((d) => d.title === 'Nota de arquitetura realtime')!;
    expect(await screen.findByRole('link', { name: /Nota de arquitetura realtime/ })).toBeInTheDocument();
    expect(screen.getByText('Aguardando aprovação')).toBeInTheDocument();

    // Aprovação via API: o mock emite document.stateChanged no stream do
    // projeto e o painel reage sem reload (invalida e refaz a query).
    await act(async () => {
      await bundle.api.transitionDocument(nota.id, { toState: 'approved' });
    });

    expect(await screen.findByText('Aprovado')).toBeInTheDocument();
    expect(screen.queryByText('Aguardando aprovação')).not.toBeInTheDocument();
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
  useUiStore.setState({ chatWorkflowPanelOpen: true });
});

describe('ChatPage — painel de workflow responsivo', () => {
  function renderChat() {
    return renderWithApi(
      <MemoryRouter>
        <ChatPage />
      </MemoryRouter>,
      createTestBundle(),
    );
  }

  it('desktop (lg+): painel lateral persistente com toggle recolhível', async () => {
    stubMatchMedia(true);
    const user = userEvent.setup();
    renderChat();

    const aside = await screen.findByRole('complementary', { name: 'Workflow do projeto' });
    expect(await within(aside).findByRole('button', { name: /Validação/ })).toBeInTheDocument();

    // Recolhe e reabre pelo toggle (estado persistido na ui-store).
    await user.click(screen.getByRole('button', { name: 'Fechar painel do workflow' }));
    expect(screen.queryByRole('complementary', { name: 'Workflow do projeto' })).toBeNull();
    expect(useUiStore.getState().chatWorkflowPanelOpen).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Abrir painel do workflow' }));
    expect(await screen.findByRole('complementary', { name: 'Workflow do projeto' })).toBeInTheDocument();
    // Sem drawer no desktop.
    expect(screen.queryByRole('dialog', { name: 'Workflow do projeto' })).toBeNull();
  });

  it('mobile (<lg): painel abre como drawer (dialog modal) e fecha com Esc', async () => {
    stubMatchMedia(false);
    const user = userEvent.setup();
    renderChat();

    // Chat não é sacrificado: nenhum aside; o botão abre o drawer.
    expect(screen.queryByRole('complementary')).toBeNull();
    await user.click(
      await screen.findByRole('button', { name: 'Abrir painel do workflow' }),
    );

    const drawer = await screen.findByRole('dialog', { name: 'Workflow do projeto' });
    expect(drawer).toHaveAttribute('aria-modal', 'true');
    expect(await within(drawer).findByRole('button', { name: /Validação/ })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog', { name: 'Workflow do projeto' })).toBeNull();
  });
});
