import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { createTestBundle } from '@/api/__tests__/test-utils';
import DocumentsPage from '@/features/documents/pages/documents-page';
import { diffLines } from '@/features/documents/lib/diff';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = createTestBundle().fixtures.data;
const specApi = fixtures.documents.find((doc) => doc.title === 'Spec da API v1')!;
const prd = fixtures.documents.find((doc) => doc.title === 'PRD do Poseidon Console')!;

function renderDocuments(initialEntry = '/documents') {
  // Bundle novo por teste: o store do mock é mutável (aprovações, classificação).
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={[initialEntry]}>
      <Routes>
        <Route path="/documents" element={<DocumentsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('diffLines', () => {
  it('marca linhas adicionadas, removidas e mantidas com numeração', () => {
    const result = diffLines('a\nb\nc', 'a\nx\nc');
    expect(result.identical).toBe(false);
    expect(result.addedCount).toBe(1);
    expect(result.removedCount).toBe(1);
    expect(result.lines).toEqual([
      { type: 'same', text: 'a', oldLine: 1, newLine: 1 },
      { type: 'removed', text: 'b', oldLine: 2, newLine: null },
      { type: 'added', text: 'x', oldLine: null, newLine: 2 },
      { type: 'same', text: 'c', oldLine: 3, newLine: 3 },
    ]);
  });

  it('detecta versões idênticas', () => {
    const result = diffLines('mesmo\ntexto', 'mesmo\ntexto');
    expect(result.identical).toBe(true);
    expect(result.lines.every((line) => line.type === 'same')).toBe(true);
  });
});

describe('DocumentsPage', () => {
  it('filtra o catálogo por estado', async () => {
    const user = userEvent.setup();
    renderDocuments();

    // Catálogo renderizado (cards mobile + tabela desktop no jsdom).
    expect((await screen.findAllByText('Spec da API v1')).length).toBeGreaterThan(0);

    await user.selectOptions(screen.getByLabelText('Estado'), 'approved');
    expect(screen.queryByText('Spec da API v1')).not.toBeInTheDocument();
    expect(screen.getAllByText('PRD do Poseidon Console').length).toBeGreaterThan(0);
  });

  it('abre o detalhe pelo deep-link ?doc= e renderiza markdown da versão vigente', async () => {
    renderDocuments(`/documents?doc=${prd.id}`);

    expect(
      await screen.findByRole('heading', { name: 'PRD do Poseidon Console' }),
    ).toBeInTheDocument();
    // Markdown renderizado (v2: "Inclui cockpit e quadro.").
    expect(screen.getByText(/Inclui cockpit e quadro/)).toBeInTheDocument();
  });

  it('reprovação de documento exige observação; com observação, resolve', async () => {
    const user = userEvent.setup();
    renderDocuments(`/documents?doc=${specApi.id}`);

    expect(
      await screen.findByRole('heading', { name: 'Spec da API v1' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Reprovar' }));
    await user.click(screen.getByRole('button', { name: 'Confirmar reprovação' }));
    expect(
      await screen.findByText('A observação é obrigatória para reprovar.'),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText(/Observação/), 'Contratos sem exemplos de payload.');
    await user.click(screen.getByRole('button', { name: 'Confirmar reprovação' }));
    expect(await screen.findByText('Reprovada')).toBeInTheDocument();
  });

  it('compara versões com diff linha a linha', async () => {
    const user = userEvent.setup();
    renderDocuments(`/documents?doc=${prd.id}`);

    expect(
      await screen.findByRole('heading', { name: 'PRD do Poseidon Console' }),
    ).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Comparar de'), '1');
    await user.selectOptions(screen.getByLabelText('para'), '2');

    expect(await screen.findByText(/linha\(s\) adicionada\(s\)/)).toBeInTheDocument();
    // Linha alterada: removida da v1 e adicionada na v2.
    expect(screen.getByText(/− # PRD/)).toBeInTheDocument();
    expect(screen.getByText(/\+ # PRD v2/)).toBeInTheDocument();
  });

  it('lista documentos órfãos e classifica com fase do workflow', async () => {
    const user = userEvent.setup();
    renderDocuments();

    const orphansSection = await screen.findByRole('region', { name: 'Documentos órfãos' });
    expect(within(orphansSection).getByText('Spec do protótipo v0')).toBeInTheDocument();
    expect(within(orphansSection).getByText('ADR 002 — SSR')).toBeInTheDocument();

    await user.click(
      within(orphansSection).getAllByRole('button', { name: 'Classificar' })[0],
    );
    const dialog = await screen.findByRole('dialog', { name: /Classificar "Spec do protótipo v0"/ });
    await user.selectOptions(within(dialog).getByLabelText(/Fase do workflow/), 'Execução');
    await user.click(within(dialog).getByRole('button', { name: 'Salvar' }));

    // Saiu da lista de órfãos; o ADR 002 permanece.
    await screen.findAllByText('ADR 002 — SSR');
    expect(
      within(screen.getByRole('region', { name: 'Documentos órfãos' })).queryByText(
        'Spec do protótipo v0',
      ),
    ).not.toBeInTheDocument();
  });

  it('atualiza o estado do documento no catálogo via document.stateChanged', async () => {
    const { bundle } = renderDocuments();

    expect((await screen.findAllByText('Guia de UX do quadro')).length).toBeGreaterThan(0);
    const doc = fixtures.documents.find((entry) => entry.title === 'Guia de UX do quadro')!;
    expect(screen.getAllByText('Em elaboração').length).toBeGreaterThan(0);

    act(() => {
      // Caminho real: outra sessão transiciona o documento — o mock emite
      // document.stateChanged no stream do projeto.
      void bundle.api.transitionDocument(doc.id, { toState: 'inReview' });
    });

    // O badge "Em elaboração" some da tabela (único doc nesse estado no projeto).
    // (o <option> do filtro de estado contém o mesmo texto — asserção escopada).
    await waitFor(() => {
      expect(
        within(screen.getByRole('table')).queryByText('Em elaboração'),
      ).not.toBeInTheDocument();
    });
    expect(within(screen.getByRole('table')).getAllByText('Em revisão').length).toBeGreaterThan(0);
  });
});
