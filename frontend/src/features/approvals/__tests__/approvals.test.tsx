import { screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import ApprovalsPage from '@/features/approvals/pages/approvals-page';
import { approvalKind, sortQueue } from '@/features/approvals/lib/approvals-derive';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = createTestBundle().fixtures.data;

/** Espião de destino: mostra para onde o redirect levou, com a busca. */
function Destination() {
  const location = useLocation();
  return <p>{`destino:${location.pathname}${location.search}`}</p>;
}

describe('approvals-derive', () => {
  it('deriva o tipo da decisão pelo vínculo preenchido', () => {
    const [gate, documento, tarefa, , decisao] = fixtures.approvals;
    expect(approvalKind(gate)).toBe('gate');
    expect(approvalKind(documento)).toBe('document');
    expect(approvalKind(tarefa)).toBe('task');
    expect(approvalKind(decisao)).toBe('decision');
  });

  it('ordena por prazo (sem prazo por último), depois criticidade', () => {
    const sorted = sortQueue(fixtures.approvals);
    // Suíte E2E (18/07) → Gate (19/07) → Spec (25/07) → sem prazo por último.
    expect(sorted[0].title).toBe('Aprovar publicação da suíte E2E');
    expect(sorted[1].title).toBe('Aprovar Gate de Qualidade');
    expect(sorted[2].title).toBe('Aprovar Spec da API v1');
    expect(sorted[sorted.length - 1].dueAt).toBeNull();
  });
});

describe('ApprovalsPage', () => {
  // D9: a tela virou atalho. A fila inteira vive na aba "Aguardando sua
  // aprovação" de Documentos — o endereço antigo continua funcionando para
  // quem chega por link, favorito ou pelo card do painel.
  it('redireciona para a aba de aprovação dentro de Documentos', async () => {
    renderWithApi(
      <MemoryRouter initialEntries={['/approvals']}>
        <Routes>
          <Route path="/approvals" element={<ApprovalsPage />} />
          <Route path="/documents" element={<Destination />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(await screen.findByText('destino:/documents?tab=approvals')).toBeInTheDocument();
  });
});
