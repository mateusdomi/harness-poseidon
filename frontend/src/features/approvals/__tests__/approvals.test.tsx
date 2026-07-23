import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import ApprovalsPage from '@/features/approvals/pages/approvals-page';
import { approvalKind, sortQueue } from '@/features/approvals/lib/approvals-derive';
import { renderWithApi } from '@/test/render-with-providers';

const fixtures = createTestBundle().fixtures.data;

function renderApprovals() {
  // Bundle novo por teste: o store do mock é mutável (aprovações).
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={['/approvals']}>
      <Routes>
        <Route path="/approvals" element={<ApprovalsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
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
  it('lista a fila pendente ordenada por prazo', async () => {
    renderApprovals();

    const queue = await screen.findByRole('list', { name: 'Fila de aprovações' });
    const items = within(queue).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(within(items[0]).getByText('Aprovar publicação da suíte E2E')).toBeInTheDocument();
    expect(within(items[1]).getByText('Aprovar Gate de Qualidade')).toBeInTheDocument();
    expect(within(items[2]).getByText('Aprovar Spec da API v1')).toBeInTheDocument();

    // Contexto (impacto/evidências) expandível no item.
    await userEvent.setup().click(
      within(items[0]).getByRole('button', { name: 'Ver impacto e evidências' }),
    );
    expect(await within(items[0]).findByText('Impacto e evidências')).toBeInTheDocument();
    expect(within(items[0]).getByText(/Suíte E2E do fluxo de aprovação/)).toBeInTheDocument();
  });

  it('filtra por criticidade e por projeto', async () => {
    const user = userEvent.setup();
    renderApprovals();

    await screen.findByRole('list', { name: 'Fila de aprovações' });

    await user.selectOptions(screen.getByLabelText('Criticidade'), 'critical');
    let queue = screen.getByRole('list', { name: 'Fila de aprovações' });
    expect(within(queue).getAllByRole('listitem')).toHaveLength(1);
    expect(within(queue).getByText('Aprovar publicação da suíte E2E')).toBeInTheDocument();

    // Projeto sem pendências → estado vazio "Nada pendente".
    await user.selectOptions(screen.getByLabelText('Criticidade'), '');
    await user.selectOptions(screen.getByLabelText('Projeto'), fixtures.projects[1].id);
    expect(await screen.findByText('Nada pendente')).toBeInTheDocument();
    queue = screen.queryByRole('list', { name: 'Fila de aprovações' }) as never;
    expect(queue).toBeNull();
  });

  it('resolve uma aprovação e remove da fila; reprovação exige observação', async () => {
    const user = userEvent.setup();
    renderApprovals();

    let queue = await screen.findByRole('list', { name: 'Fila de aprovações' });
    expect(within(queue).getAllByRole('listitem')).toHaveLength(3);

    // Reprovar sem observação → erro.
    const firstItem = within(queue).getAllByRole('listitem')[0];
    await user.click(within(firstItem).getByRole('button', { name: 'Reprovar' }));
    await user.click(within(firstItem).getByRole('button', { name: 'Confirmar reprovação' }));
    expect(
      await within(firstItem).findByText('A observação é obrigatória para reprovar.'),
    ).toBeInTheDocument();

    // Cancela a reprovação e aprova → sai da fila.
    await user.click(within(firstItem).getByRole('button', { name: 'Cancelar' }));
    queue = screen.getByRole('list', { name: 'Fila de aprovações' });
    await user.click(
      within(within(queue).getAllByRole('listitem')[0]).getByRole('button', { name: 'Aprovar' }),
    );
    await waitFor(() => {
      expect(
        within(screen.getByRole('list', { name: 'Fila de aprovações' })).getAllByRole('listitem'),
      ).toHaveLength(2);
    });
  });

  it('approval.requested adiciona à fila em tempo real e resolved remove', async () => {
    const { bundle } = renderApprovals();

    let queue = await screen.findByRole('list', { name: 'Fila de aprovações' });
    expect(within(queue).getAllByRole('listitem')).toHaveLength(3);

    const project = bundle.fixtures.data.projects[0];
    const chief = bundle.fixtures.data.projects[0].chiefAgentId;
    let createdId = '';
    await act(async () => {
      const created = await bundle.api.create('approvals', {
        projectId: project.id,
        title: 'Aprovar mudança de modo para autônomo',
        description: 'Chefe pediu confirmação para operar sem supervisão.',
        requestedByAgentId: chief,
        priority: 'high',
      });
      createdId = created.id;
    });

    queue = await screen.findByRole('list', { name: 'Fila de aprovações' });
    expect(
      await within(queue).findByText('Aprovar mudança de modo para autônomo'),
    ).toBeInTheDocument();

    await act(async () => {
      await bundle.api.resolveApproval(createdId, { decision: 'approved' });
    });
    await waitFor(() => {
      expect(
        screen.queryByText('Aprovar mudança de modo para autônomo'),
      ).not.toBeInTheDocument();
    });
  });
});
