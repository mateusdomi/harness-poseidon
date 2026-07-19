import { fireEvent, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { createTestBundle } from '@/api/__tests__/test-utils';
import WorkflowsPage from '@/features/workflows/pages/workflows-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderWorkflows() {
  // Bundle novo por teste: o store do mock é mutável (modo, versões).
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/workflows']}>
      <Routes>
        <Route path="/workflows" element={<WorkflowsPage />} />
        <Route path="/documents" element={<p>DOCUMENTOS</p>} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

describe('WorkflowsPage', () => {
  it('renderiza o stepper de fases com status, gates e documentos por fase', async () => {
    renderWorkflows();

    expect(
      await screen.findByRole('heading', { name: 'Fases do workflow' }),
    ).toBeInTheDocument();

    const stepper = screen.getByRole('list', { name: 'Fases do workflow' });
    for (const phase of ['Planejamento', 'Execução', 'Validação', 'Publicação']) {
      expect(within(stepper).getByText(new RegExp(phase))).toBeInTheDocument();
    }

    // Estados das fases vêm do run (Planejamento concluída, Validação ativa).
    const faseValidacao = within(stepper)
      .getAllByRole('listitem')
      .find((item) => within(item).queryByText(/Validação/) !== null)!;
    expect(within(faseValidacao).getByText('Ativa')).toBeInTheDocument();
    expect(within(faseValidacao).getByText('Gate de Qualidade')).toBeInTheDocument();
    expect(within(faseValidacao).getByText('Pendente')).toBeInTheDocument();

    // Documento vinculado à fase de Validação (deep-link para o catálogo).
    const docLink = within(faseValidacao).getByRole('link', {
      name: 'Nota de arquitetura realtime',
    });
    expect(docLink).toHaveAttribute(
      'href',
      expect.stringContaining('/documents?doc='),
    );

    // Explicação de que o run permanece na versão original.
    expect(screen.getByText(/segue a versão v1/)).toBeInTheDocument();
  });

  it('troca de modo exige aceite de risco com justificativa', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    await user.click(await screen.findByRole('button', { name: 'Alterar modo' }));
    expect(
      await screen.findByRole('dialog', { name: 'Trocar modo de operação' }),
    ).toBeInTheDocument();

    // Confirmar sem checkbox nem justificativa → erro de aceite.
    await user.click(screen.getByRole('button', { name: 'Confirmar troca' }));
    expect(
      await screen.findByText('Marque o aceite de risco e escreva a justificativa.'),
    ).toBeInTheDocument();

    await user.click(
      screen.getByRole('checkbox', { name: 'Li e aceito os riscos deste modo de operação' }),
    );
    await user.click(screen.getByRole('button', { name: 'Confirmar troca' }));
    expect(
      await screen.findByText('Marque o aceite de risco e escreva a justificativa.'),
    ).toBeInTheDocument();

    await user.type(screen.getByLabelText(/Justificativa do aceite/), 'Sprint crítica, aceito o risco.');
    await user.selectOptions(screen.getByLabelText('Novo modo'), 'manual');
    await user.click(screen.getByRole('button', { name: 'Confirmar troca' }));

    // Dialog fecha e o aceite fica registrado.
    expect(
      await screen.findByText(/aceite\(s\) de risco registrado\(s\)/),
    ).toBeInTheDocument();
  });

  it('modo semiautônomo exige seleção de gates que pausam', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    await user.click(await screen.findByRole('button', { name: 'Alterar modo' }));
    await user.selectOptions(await screen.findByLabelText('Novo modo'), 'semiautonomous');
    await user.click(
      screen.getByRole('checkbox', { name: 'Li e aceito os riscos deste modo de operação' }),
    );
    await user.type(screen.getByLabelText(/Justificativa do aceite/), 'Confio nos agentes, com pausa no release.');

    // Sem gate selecionado → erro específico.
    await user.click(screen.getByRole('button', { name: 'Confirmar troca' }));
    expect(
      await screen.findByText('Selecione ao menos um gate que pausa para aprovação.'),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('checkbox', { name: 'Gate de Release' }));
    await user.click(screen.getByRole('button', { name: 'Confirmar troca' }));

    expect(await screen.findByText(/Pausa nos gates: Gate de Release/)).toBeInTheDocument();
  });

  it('publica nova versão de template e lista a versão publicada', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    await user.click(await screen.findByRole('button', { name: 'Nova versão' }));
    const dialog = await screen.findByRole('dialog', { name: /Nova versão — Fluxo de Entrega Padrão/ });

    const phasesField = within(dialog).getByLabelText(/Fases \(uma por linha/) as HTMLTextAreaElement;
    fireEvent.change(phasesField, {
      target: { value: `${phasesField.value}\nPós-produção` },
    });
    await user.click(within(dialog).getByRole('button', { name: 'Publicar versão' }));

    // v2 publicada com 5 fases; vira a versão atual do template.
    expect(await screen.findByText('5 fases')).toBeInTheDocument();
    const templatesSection = screen.getByRole('region', { name: 'Templates de workflow' });
    expect(within(templatesSection).getByText('v2')).toBeInTheDocument();
    expect(within(templatesSection).getByText('Atual')).toBeInTheDocument();
  });
});
