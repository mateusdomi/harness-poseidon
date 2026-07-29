import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import WorkflowsPage from '@/features/workflows/pages/workflows-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderWorkflows() {
  // Bundle novo por teste: o store do mock é mutável (modo, versões).
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={['/workflows']}>
      <Routes>
        <Route path="/workflows" element={<WorkflowsPage />} />
        <Route path="/documents" element={<p>DOCUMENTOS</p>} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

/** Card (li) de um template pelo nome, dentro da seção de templates. */
async function templateCard(name: string) {
  const section = await screen.findByRole('region', { name: 'Modelos de fluxo de trabalho' });
  const heading = await within(section).findByRole('heading', { name });
  return within(heading.closest('li')!);
}

describe('WorkflowsPage', () => {
  it('renderiza o stepper de fases com status, gates e documentos por fase', async () => {
    renderWorkflows();

    expect(
      await screen.findByRole('heading', { name: 'Etapas do fluxo de trabalho' }),
    ).toBeInTheDocument();

    const stepper = screen.getByRole('list', { name: 'Etapas do fluxo de trabalho' });
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
    expect(
      within(faseValidacao).getByRole('progressbar', {
        name: 'Progresso da etapa Validação',
      }),
    ).toHaveAttribute('aria-valuenow', '0');
    expect(within(faseValidacao).getByText('Relatório de testes')).toBeInTheDocument();
    expect(within(faseValidacao).getByText('Evidências')).toBeInTheDocument();

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
      await screen.findByText('Selecione ao menos um ponto de aprovação que pause para a sua decisão.'),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('checkbox', { name: 'Gate de Release' }));
    await user.click(screen.getByRole('button', { name: 'Confirmar troca' }));

    expect(await screen.findByText(/Pausa nos pontos de aprovação: Gate de Release/)).toBeInTheDocument();
  });
});

describe('TemplateAdmin (FR-4)', () => {
  it('cria template do zero como rascunho', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    await user.click(await screen.findByRole('button', { name: 'Novo modelo de fluxo' }));
    const dialog = await screen.findByRole('dialog', { name: 'Novo modelo de fluxo de trabalho' });
    await user.type(within(dialog).getByLabelText(/Nome/), 'Fluxo Sob Medida');
    await user.type(within(dialog).getByLabelText('Descrição'), 'Template criado do zero.');
    await user.click(within(dialog).getByRole('button', { name: 'Criar modelo de fluxo' }));

    const card = await templateCard('Fluxo Sob Medida');
    expect(card.getByText('Rascunho')).toBeInTheDocument();
    expect(card.getByText('Template criado do zero.')).toBeInTheDocument();
  });

  it('edita fase do rascunho, publica congelando e mostra banner de impacto', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    const card = await templateCard('Fluxo de Entrega Padrão');
    await user.click(card.getByRole('button', { name: 'Novo rascunho' }));

    const dialog = await screen.findByRole('dialog', {
      name: 'Editar rascunho — Fluxo de Entrega Padrão (v2)',
    });

    // Banner: a execução ativa permanece na versão em uso (v1).
    expect(
      within(dialog).getByText(/execução ativa do projeto permanece na v1/),
    ).toBeInTheDocument();

    // Edita o nome da 4ª fase e salva o rascunho (segue editável).
    const names = within(dialog).getAllByLabelText(/Nome da etapa/);
    await user.clear(names[3]);
    await user.type(names[3], 'Publicação Final');
    await user.click(within(dialog).getByRole('button', { name: 'Salvar rascunho' }));
    expect(await within(dialog).findByText('Rascunho salvo.')).toBeInTheDocument();

    // Publicar congela: v2 nasce publicada e vira a vigente.
    await user.click(within(dialog).getByRole('button', { name: 'Publicar versão' }));

    const updatedCard = await templateCard('Fluxo de Entrega Padrão');
    expect(updatedCard.getByText('v2')).toBeInTheDocument();
    expect(updatedCard.getByText('Atual')).toBeInTheDocument();
    // Badge "Publicada" no template e na v2.
    expect(updatedCard.getAllByText('Publicada').length).toBeGreaterThanOrEqual(2);
  });

  it('validação do Harness impede publicar rascunho inválido', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    const card = await templateCard('Fluxo de Entrega Padrão');
    await user.click(card.getByRole('button', { name: 'Novo rascunho' }));
    const dialog = await screen.findByRole('dialog', {
      name: 'Editar rascunho — Fluxo de Entrega Padrão (v2)',
    });

    // Fase duplicada: renomeia a 4ª fase com o nome da 3ª.
    const names = within(dialog).getAllByLabelText(/Nome da etapa/);
    await user.clear(names[3]);
    await user.type(names[3], 'Validação');
    await user.click(within(dialog).getByRole('button', { name: 'Publicar versão' }));

    // Publicação bloqueada com erros i18n claros; o dialog permanece aberto.
    expect(
      await within(dialog).findByText('A versão não passou na validação do Harness:'),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('Etapa duplicada: Validação.')).toBeInTheDocument();
    expect(
      screen.getByRole('dialog', { name: 'Editar rascunho — Fluxo de Entrega Padrão (v2)' }),
    ).toBeInTheDocument();
  });

  it('duplica versão publicada como novo rascunho', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    const card = await templateCard('Fluxo de Entrega Padrão');
    // Duplicar da LINHA DA VERSÃO (o primeiro "Duplicar" é o do template).
    const duplicateButtons = card.getAllByRole('button', { name: 'Duplicar' });
    await user.click(duplicateButtons[1]);

    const updatedCard = await templateCard('Fluxo de Entrega Padrão');
    expect(updatedCard.getByText('v2')).toBeInTheDocument();
    expect(updatedCard.getByText('Rascunho')).toBeInTheDocument();
    // A vigente continua sendo a v1 publicada.
    expect(updatedCard.getAllByText('Publicada').length).toBeGreaterThanOrEqual(1);
  });

  it('exclui rascunho nunca utilizado com confirmação', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    const card = await templateCard('Fluxo Experimental');
    const deleteButtons = card.getAllByRole('button', { name: 'Excluir' });
    // Excluir da LINHA DA VERSÃO (rascunho v1, nunca utilizado).
    await user.click(deleteButtons[1]);

    const dialog = await screen.findByRole('dialog', { name: 'Excluir rascunho' });
    await user.click(within(dialog).getByRole('button', { name: 'Excluir rascunho' }));

    const updatedCard = await templateCard('Fluxo Experimental');
    expect(updatedCard.queryByText('v1')).not.toBeInTheDocument();
  });

  it('bloqueia excluir versão publicada e template utilizado (UI explica)', async () => {
    renderWorkflows();

    const card = await templateCard('Fluxo de Entrega Padrão');
    const deleteButtons = card.getAllByRole('button', { name: 'Excluir' });
    // Template em uso + versão publicada: ambos desabilitados com explicação.
    expect(deleteButtons[0]).toBeDisabled();
    expect(deleteButtons[0]).toHaveAttribute(
      'title',
      expect.stringContaining('rascunho nunca utilizados'),
    );
    expect(deleteButtons[1]).toBeDisabled();
    expect(deleteButtons[1]).toHaveAttribute(
      'title',
      expect.stringContaining('publicadas são imutáveis'),
    );
  });

  it('mostra as fases da versão como chips no card do template', async () => {
    renderWorkflows();

    const card = await templateCard('Fluxo de Entrega Padrão');
    const chips = await card.findByRole('list', { name: 'Etapas desta versão' });
    for (const phase of ['Planejamento', 'Execução', 'Validação', 'Publicação']) {
      expect(within(chips).getByText(phase)).toBeInTheDocument();
    }
  });

  it('compara duas versões e mostra o diff estrutural', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    // Publica uma v2 com fase renomeada (cria diff real).
    const card = await templateCard('Fluxo de Entrega Padrão');
    await user.click(card.getByRole('button', { name: 'Novo rascunho' }));
    const editor = await screen.findByRole('dialog', {
      name: 'Editar rascunho — Fluxo de Entrega Padrão (v2)',
    });
    const names = within(editor).getAllByLabelText(/Nome da etapa/);
    await user.clear(names[3]);
    await user.type(names[3], 'Publicação Final');
    await user.click(within(editor).getByRole('button', { name: 'Publicar versão' }));

    const updatedCard = await templateCard('Fluxo de Entrega Padrão');
    await user.click(updatedCard.getByRole('checkbox', { name: 'Selecionar v1 para comparar' }));
    await user.click(updatedCard.getByRole('checkbox', { name: 'Selecionar v2 para comparar' }));

    const section = screen.getByRole('region', { name: 'Modelos de fluxo de trabalho' });
    await user.click(within(section).getByRole('button', { name: 'Comparar versões' }));

    const dialog = await screen.findByRole('dialog', { name: 'Comparar versões' });
    // Cada lado é rotulado com nome + versão para desambiguar (não "v1 → v1").
    expect(within(dialog).getByText('Origem (antes)')).toBeInTheDocument();
    expect(within(dialog).getByText('Destino (depois)')).toBeInTheDocument();
    // Diff explícito sobre de qual lado veio o quê.
    expect(
      within(dialog).getByText('Etapas adicionadas em Fluxo de Entrega Padrão v2'),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('+ Publicação Final')).toBeInTheDocument();
    expect(
      within(dialog).getByText('Etapas removidas de Fluxo de Entrega Padrão v1'),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('− Publicação')).toBeInTheDocument();
  });

  it('arquiva template (tombstone) preservando-o na lista', async () => {
    const user = userEvent.setup();
    renderWorkflows();

    const card = await templateCard('Fluxo Experimental');
    // Arquivar do TEMPLATE (o primeiro; o outro é o da versão rascunho).
    await user.click(card.getAllByRole('button', { name: 'Arquivar' })[0]);

    const updatedCard = await templateCard('Fluxo Experimental');
    expect(updatedCard.getByText('Arquivada')).toBeInTheDocument();
    // Arquivado não permite novo rascunho.
    expect(updatedCard.queryByRole('button', { name: 'Novo rascunho' })).not.toBeInTheDocument();
  });

  it('vincular ao projeto ativo fica bloqueado quando o projeto já tem workflow', async () => {
    renderWorkflows();

    const card = await templateCard('Fluxo Experimental');
    const link = card.getByRole('button', { name: 'Vincular ao projeto ativo' });
    expect(link).toBeDisabled();
    expect(link).toHaveAttribute('title', 'O projeto ativo já tem um fluxo de trabalho vinculado.');
  });
});
