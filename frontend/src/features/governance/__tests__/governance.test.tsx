import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { createTestBundle } from '@/api/__tests__/test-utils';
import GovernancePage from '@/features/governance/pages/governance-page';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = createTestBundle().fixtures.data;

function renderGovernance() {
  // Bundle novo por teste: o store do mock é mutável (audit-events).
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/governance']}>
      <Routes>
        <Route path="/governance" element={<GovernancePage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

function timeline() {
  return screen.getByRole('list', { name: 'Linha do tempo de auditoria' });
}

describe('GovernancePage', () => {
  it('lista a trilha de auditoria com ator, ação, alvo resolvido e nota de segredos', async () => {
    renderGovernance();

    const list = await screen.findByRole('list', { name: 'Linha do tempo de auditoria' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(8);

    // Ação em mono, tipo de ator em badge e alvo resolvido pelo catálogo.
    expect(within(list).getAllByText('approval.resolved')).toHaveLength(2);
    expect(within(list).getByText('Aprovar PRD do console')).toBeInTheDocument();
    expect(within(list).getAllByText('Você').length).toBeGreaterThan(0);
    expect(within(list).getByText('Sistema')).toBeInTheDocument();

    // Nota de mascaramento visível uma vez na tela.
    expect(
      screen.getByText('Revelado no backend somente com permissão.'),
    ).toBeInTheDocument();
  });

  it('filtra por busca textual (ação/detalhe) e mostra estado vazio sem resultado', async () => {
    const user = userEvent.setup();
    renderGovernance();

    await screen.findByRole('list', { name: 'Linha do tempo de auditoria' });

    await user.type(screen.getByLabelText('Buscar'), 'budget');
    expect(within(timeline()).getAllByRole('listitem')).toHaveLength(1);
    expect(within(timeline()).getByText('Aprovar aumento de budget')).toBeInTheDocument();

    await user.clear(screen.getByLabelText('Buscar'));
    await user.type(screen.getByLabelText('Buscar'), 'zzz-inexistente');
    expect(await screen.findByText('Nenhum evento encontrado')).toBeInTheDocument();

    // Exportação desabilitada sem eventos filtrados.
    expect(screen.getByRole('button', { name: /Exportar JSON/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Exportar CSV/ })).toBeDisabled();
  });

  it('filtra por tipo de ator e por projeto (evento de licença não é correlacionável)', async () => {
    const user = userEvent.setup();
    renderGovernance();

    await screen.findByRole('list', { name: 'Linha do tempo de auditoria' });

    await user.selectOptions(screen.getByLabelText('Tipo de ator'), 'system');
    let items = within(timeline()).getAllByRole('listitem');
    expect(items).toHaveLength(1);
    expect(within(items[0]).getByText('license.validated')).toBeInTheDocument();

    // Projeto Poseidon: todos os eventos correlacionáveis — a licença fica de fora.
    await user.selectOptions(screen.getByLabelText('Tipo de ator'), '');
    const poseidon = fixtures.projects.find((project) => project.name === 'Poseidon Frontend')!;
    await user.selectOptions(screen.getByLabelText('Projeto'), poseidon.id);
    items = within(timeline()).getAllByRole('listitem');
    expect(items).toHaveLength(7);
    expect(within(timeline()).queryByText('license.validated')).toBeNull();
  });

  it('expande o evento com detalhe mascarado e correlação da aprovação', async () => {
    const user = userEvent.setup();
    renderGovernance();

    const list = await screen.findByRole('list', { name: 'Linha do tempo de auditoria' });
    const approvalItem = within(list)
      .getAllByRole('listitem')
      .find((item) => within(item).queryByText('Aprovar PRD do console') !== null)!;

    await user.click(within(approvalItem).getByRole('button', { name: 'Ver detalhes e correlação' }));

    expect(within(approvalItem).getByText('PRD do console aprovado.')).toBeInTheDocument();
    expect(within(approvalItem).getByText('Correlação')).toBeInTheDocument();
    expect(within(approvalItem).getByText('Aprovada')).toBeInTheDocument();
    expect(within(approvalItem).getByText('Aprovado sem ressalvas.')).toBeInTheDocument();

    // Recolhe de volta.
    await user.click(within(approvalItem).getByRole('button', { name: 'Ocultar detalhes' }));
    expect(within(approvalItem).queryByText('Correlação')).toBeNull();
  });

  it('audit.eventAppended no stream global adiciona o evento em tempo real', async () => {
    const { bundle } = renderGovernance();

    await screen.findByRole('list', { name: 'Linha do tempo de auditoria' });
    expect(within(timeline()).getAllByRole('listitem')).toHaveLength(8);

    const workflow = bundle.fixtures.data.workflows[0];
    await act(async () => {
      await bundle.api.setWorkflowOperationMode(workflow.id, {
        mode: 'semiautonomous',
        semiautonomousPauseGates: [],
        riskAcceptanceNote: 'Aceite registrado em teste.',
      });
    });

    await waitFor(() => {
      expect(within(timeline()).getAllByRole('listitem')).toHaveLength(9);
    });
  });

  it('exporta os eventos filtrados em JSON e CSV (download client-side)', async () => {
    const user = userEvent.setup();
    const createObjectURL = vi.fn<(blob: Blob) => string>(() => 'blob:mock');
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL });
    // jsdom não navega: o click do <a download> é neutralizado no teste.
    const clickSpy = vi
      .spyOn(HTMLAnchorElement.prototype, 'click')
      .mockImplementation(() => {});

    renderGovernance();
    await screen.findByRole('list', { name: 'Linha do tempo de auditoria' });

    await user.click(screen.getByRole('button', { name: /Exportar JSON/ }));
    expect(createObjectURL).toHaveBeenCalledTimes(1);
    expect(createObjectURL.mock.calls[0][0]).toBeInstanceOf(Blob);

    await user.click(screen.getByRole('button', { name: /Exportar CSV/ }));
    expect(createObjectURL).toHaveBeenCalledTimes(2);

    clickSpy.mockRestore();
    vi.unstubAllGlobals();
  });
});
