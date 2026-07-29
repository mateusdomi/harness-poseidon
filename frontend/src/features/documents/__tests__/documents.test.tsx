import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import DocumentsPage from '@/features/documents/pages/documents-page';
import { diffLines } from '@/features/documents/lib/diff';
import { renderWithApi } from '@/test/render-with-providers';
import { useActiveProjectStore } from '@/stores/active-project-store';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

const fixtures = createTestBundle().fixtures.data;
const specApi = fixtures.documents.find((doc) => doc.title === 'Spec da API v1')!;
const prd = fixtures.documents.find((doc) => doc.title === 'PRD do Poseidon Console')!;

function renderDocuments(initialEntry = '/documents') {
  // Bundle novo por teste: o store do mock é mutável (aprovações, classificação).
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route path="/documents" element={<DocumentsPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

// Modo de apresentação e seleção de projeto são estado de MÓDULO, persistido
// entre testes: sem reset, o teste que sobe para Técnico contaminaria os
// seguintes (o padrão do produto é Negócio), e a seleção de um projeto criado
// num bundle anterior deixaria os próximos sem projeto ativo.
beforeEach(() => {
  usePresentationStore.setState({ modeByProfile: {} });
  useActiveProjectStore.setState({ selectionsByProfile: {} });
  useSessionStore.setState({ activeProfileId: null });
});

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
    await user.selectOptions(within(dialog).getByLabelText(/Etapa do fluxo de trabalho/), 'Execução');
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

  it('copia o conteúdo da versão vigente com feedback i18n', async () => {
    const user = userEvent.setup();
    // Stub depois do setup: o userEvent instala o próprio clipboard.
    const writeText = vi.fn<(text: string) => Promise<void>>().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText },
      configurable: true,
    });
    renderDocuments(`/documents?doc=${prd.id}`);

    expect(
      await screen.findByRole('heading', { name: 'PRD do Poseidon Console' }),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Copiar conteúdo' }));

    expect(writeText).toHaveBeenCalledWith('# PRD v2\n\nInclui cockpit e quadro.');
    // Botão (texto visível) + live region sr-only anunciam a cópia.
    expect((await screen.findAllByText('Conteúdo copiado')).length).toBeGreaterThan(0);
  });

  it('edita manualmente e salva como nova versão de origem humana', async () => {
    const user = userEvent.setup();
    const guia = fixtures.documents.find((doc) => doc.title === 'Guia de UX do quadro')!;
    renderDocuments(`/documents?doc=${guia.id}`);

    expect(
      await screen.findByRole('heading', { name: 'Guia de UX do quadro' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Editar' }));
    const editor = screen.getByLabelText(/Conteúdo \(markdown\)/);
    expect(editor).toHaveValue('# Guia de UX\n\nColunas, drag-and-drop e estados vazios.');

    // Cancelar descarta: nada muda.
    await user.clear(editor);
    await user.type(editor, '# Guia de UX v2\n\nRascunho descartado.');
    await user.click(screen.getByRole('button', { name: 'Cancelar' }));
    expect(screen.getByText(/Colunas, drag-and-drop e estados vazios/)).toBeInTheDocument();

    // Editar de verdade e salvar nova versão.
    await user.click(screen.getByRole('button', { name: 'Editar' }));
    const editor2 = screen.getByLabelText(/Conteúdo \(markdown\)/);
    await user.clear(editor2);
    await user.type(editor2, '# Guia de UX v2\n\nRevisão manual do conteúdo.');
    await user.click(screen.getByRole('button', { name: 'Salvar nova versão' }));

    // Sai do modo de edição, markdown da nova versão e histórico com origem humana.
    expect(await screen.findByText(/Revisão manual do conteúdo/)).toBeInTheDocument();
    const versionsSection = screen.getByRole('region', {
      name: /Versões/,
    });
    // "v2" aparece no item do histórico e nas opções de diff.
    expect(within(versionsSection).getAllByText('v2').length).toBeGreaterThan(0);
    expect(within(versionsSection).getAllByText('Você').length).toBeGreaterThan(0);
  });

  it('salvar e aprovar encadeia nova versão + aprovação pendente', async () => {
    const user = userEvent.setup();
    renderDocuments(`/documents?doc=${specApi.id}`);

    expect(
      await screen.findByRole('heading', { name: 'Spec da API v1' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Editar' }));
    const editor = screen.getByLabelText(/Conteúdo \(markdown\)/);
    await user.clear(editor);
    await user.type(editor, '# Spec API v1\n\nContratos REST corrigidos manualmente.');
    await user.click(screen.getByRole('button', { name: 'Salvar e aprovar' }));

    // Aprovação resolvida como aprovada, documento aprovado e nova versão humana.
    expect(await screen.findByText('Aprovada')).toBeInTheDocument();
    expect(await screen.findByText(/Corrigidos manualmente/i)).toBeInTheDocument();
    const versionsSection = screen.getByRole('region', { name: /Versões/ });
    expect(within(versionsSection).getAllByText('v2').length).toBeGreaterThan(0);
    expect(within(versionsSection).getAllByText('Você').length).toBeGreaterThan(0);
  });

  it('documento aprovado não oferece edição manual (exige reabrir o fluxo)', async () => {
    renderDocuments(`/documents?doc=${prd.id}`);

    expect(
      await screen.findByRole('heading', { name: 'PRD do Poseidon Console' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument();
    // A leitura e a cópia continuam disponíveis.
    expect(screen.getByRole('button', { name: 'Copiar conteúdo' })).toBeInTheDocument();
  });

  it('cria um documento do zero e abre o detalhe (fluxo criar → elaborar → editar)', async () => {
    const user = userEvent.setup();
    renderDocuments();

    // O botão do cabeçalho abre o diálogo de criação.
    await user.click((await screen.findAllByRole('button', { name: 'Criar documento' }))[0]);
    const dialog = await screen.findByRole('dialog', { name: 'Novo documento' });

    await user.type(within(dialog).getByLabelText(/Título/), 'Plano de rollout');
    await user.type(
      within(dialog).getByLabelText(/Conteúdo \(markdown\)/),
      '# Rollout\n\nEtapas do lançamento.',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Criar documento' }));

    // Abre o detalhe do novo documento (estado inicial "Planejado").
    expect(
      await screen.findByRole('heading', { name: 'Plano de rollout' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/Etapas do lançamento/)).toBeInTheDocument();
    expect(screen.getAllByText('Planejado').length).toBeGreaterThan(0);

    // Planejado não é editável: inicia a elaboração para destravar a edição.
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Iniciar elaboração' }));
    expect(await screen.findByRole('button', { name: 'Editar' })).toBeInTheDocument();
  });

  it('descarta um documento com confirmação (transição honesta para não aplicável)', async () => {
    const user = userEvent.setup();
    const guia = fixtures.documents.find((doc) => doc.title === 'Guia de UX do quadro')!;
    renderDocuments(`/documents?doc=${guia.id}`);

    expect(
      await screen.findByRole('heading', { name: 'Guia de UX do quadro' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Descartar documento' }));
    const dialog = await screen.findByRole('dialog', { name: /Descartar "Guia de UX do quadro"/ });
    await user.click(within(dialog).getByRole('button', { name: 'Descartar' }));

    // O documento passa a "Não aplicável" e a ação de descartar some.
    expect(await screen.findByText('Não aplicável')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Descartar documento' })).not.toBeInTheDocument();
  });

  it('estado vazio de filtros oferece limpar filtros', async () => {
    const user = userEvent.setup();
    renderDocuments();

    expect((await screen.findAllByText('Spec da API v1')).length).toBeGreaterThan(0);

    // Combinação de filtros que não bate com nenhum documento do projeto.
    await user.selectOptions(screen.getByLabelText('Categoria'), 'runbook');
    await user.selectOptions(screen.getByLabelText('Estado'), 'approved');

    expect(await screen.findByText('Nenhum documento corresponde aos filtros')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Limpar filtros' }));
    expect((await screen.findAllByText('Spec da API v1')).length).toBeGreaterThan(0);
  });

  it('estado vazio orientado quando o projeto não tem documentos', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    // Projeto novo sem documentos: o empty-state deve orientar, não só informar.
    const empty = await bundle.api.create('projects', {
      organizationId: bundle.fixtures.data.organizations[0].id,
      name: 'Projeto Vazio',
      key: 'VAZIO',
      description: 'Sem documentos ainda.',
    });

    renderWithApi(
      <MemoryRouter initialEntries={['/documents']}>
        <Routes>
          <Route path="/documents" element={<DocumentsPage />} />
        </Routes>
      </MemoryRouter>,
      bundle,
    );

    // Seleciona o projeto vazio no seletor.
    await screen.findByLabelText('Projeto ativo');
    await user.selectOptions(screen.getByLabelText('Projeto ativo'), empty.id);

    expect(
      await screen.findByText('Ainda não há documentos neste projeto'),
    ).toBeInTheDocument();
    // Orienta sobre como os documentos surgem.
    expect(screen.getByText(/artefatos versionados do projeto/)).toBeInTheDocument();
    // Os arquivos internos do sistema são assunto técnico (D9): no modo
    // Negócio o atalho para Documentos de Governança não existe.
    expect(
      screen.queryByRole('link', { name: 'Ir para Documentos de Governança' }),
    ).not.toBeInTheDocument();
    // E expõe as ações de criar/enviar o primeiro documento.
    expect(screen.getByRole('button', { name: 'Enviar arquivo' })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Criar documento' }).length).toBeGreaterThan(0);
  });

  it('modo técnico recupera o atalho para os documentos de governança', async () => {
    const user = userEvent.setup();
    const bundle = createTestBundle();
    const empty = await bundle.api.create('projects', {
      organizationId: bundle.fixtures.data.organizations[0].id,
      name: 'Projeto Vazio',
      key: 'VAZIO',
      description: 'Sem documentos ainda.',
    });
    useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
    usePresentationStore
      .getState()
      .requestMode(bundle.fixtures.meta.currentProfileId, 'technical');

    renderWithApi(
      <MemoryRouter initialEntries={['/documents']}>
        <Routes>
          <Route path="/documents" element={<DocumentsPage />} />
        </Routes>
      </MemoryRouter>,
      bundle,
    );

    await screen.findByLabelText('Projeto ativo');
    await user.selectOptions(screen.getByLabelText('Projeto ativo'), empty.id);

    expect(
      await screen.findByRole('link', { name: 'Ir para Documentos de Governança' }),
    ).toHaveAttribute('href', '/governance-docs');
  });
});

/* ---- D9: a tela única e a aba onde a decisão do dono acontece ---- */

describe('DocumentsPage — aba "Aguardando sua aprovação"', () => {
  it('abre pela URL ?tab=approvals com a fila ordenada por prazo', async () => {
    renderDocuments('/documents?tab=approvals');

    const queue = await screen.findByRole('list', { name: 'Fila de aprovações' });
    const items = within(queue).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(within(items[0]).getByText('Aprovar publicação da suíte E2E')).toBeInTheDocument();
    expect(within(items[1]).getByText('Aprovar Gate de Qualidade')).toBeInTheDocument();
    expect(within(items[2]).getByText('Aprovar Spec da API v1')).toBeInTheDocument();

    // A fila continua trazendo TODO tipo de decisão do projeto, com impacto e
    // evidências — nada se perdeu na fusão das telas.
    await userEvent.setup().click(
      within(items[0]).getByRole('button', { name: 'Ver impacto e evidências' }),
    );
    expect(await within(items[0]).findByText('Impacto e evidências')).toBeInTheDocument();
    expect(within(items[0]).getByText(/Suíte E2E do fluxo de aprovação/)).toBeInTheDocument();
  });

  it('a aba é alcançável por clique e o catálogo é o padrão', async () => {
    const user = userEvent.setup();
    renderDocuments();

    // Padrão: catálogo selecionado, e a aba de aprovação anuncia 3 pendências.
    const catalogTab = await screen.findByRole('tab', { name: /Documentos do projeto/ });
    const approvalsTab = screen.getByRole('tab', { name: /Aguardando sua aprovação/ });
    expect(catalogTab).toHaveAttribute('aria-selected', 'true');
    expect(approvalsTab).toHaveAttribute('aria-selected', 'false');
    expect(within(approvalsTab).getByText('3')).toBeInTheDocument();

    await user.click(approvalsTab);
    expect(await screen.findByRole('list', { name: 'Fila de aprovações' })).toBeInTheDocument();
    expect(
      screen.getByRole('tab', { name: /Aguardando sua aprovação/ }),
    ).toHaveAttribute('aria-selected', 'true');
    // O catálogo sai de cena: uma tela, dois painéis.
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('aprova na própria aba e o item sai da fila; reprovar exige observação', async () => {
    const user = userEvent.setup();
    renderDocuments('/documents?tab=approvals');

    let queue = await screen.findByRole('list', { name: 'Fila de aprovações' });
    expect(within(queue).getAllByRole('listitem')).toHaveLength(3);

    const firstItem = within(queue).getAllByRole('listitem')[0];
    await user.click(within(firstItem).getByRole('button', { name: 'Reprovar' }));
    await user.click(within(firstItem).getByRole('button', { name: 'Confirmar reprovação' }));
    expect(
      await within(firstItem).findByText('A observação é obrigatória para reprovar.'),
    ).toBeInTheDocument();

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

  it('filtra a fila por criticidade e mostra "Nada pendente" quando esvazia', async () => {
    const user = userEvent.setup();
    renderDocuments('/documents?tab=approvals');

    await screen.findByRole('list', { name: 'Fila de aprovações' });
    await user.selectOptions(screen.getByLabelText('Criticidade'), 'critical');

    const queue = screen.getByRole('list', { name: 'Fila de aprovações' });
    expect(within(queue).getAllByRole('listitem')).toHaveLength(1);
    expect(within(queue).getByText('Aprovar publicação da suíte E2E')).toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('Prazo'), 'none');
    expect(await screen.findByText('Nada pendente')).toBeInTheDocument();
  });

  it('approval.requested aparece na aba em tempo real e resolved remove', async () => {
    const { bundle } = renderDocuments('/documents?tab=approvals');

    const queue = await screen.findByRole('list', { name: 'Fila de aprovações' });
    expect(within(queue).getAllByRole('listitem')).toHaveLength(3);

    const project = bundle.fixtures.data.projects[0];
    let createdId = '';
    await act(async () => {
      const created = await bundle.api.create('approvals', {
        projectId: project.id,
        title: 'Aprovar mudança de modo para autônomo',
        description: 'A Bruna pediu confirmação para operar sem supervisão.',
        requestedByAgentId: project.chiefAgentId,
        priority: 'high',
      });
      createdId = created.id;
    });

    expect(
      await screen.findByText('Aprovar mudança de modo para autônomo'),
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

  it('inconsistência e dispensa são leitura técnica: não existem no modo Negócio', async () => {
    renderDocuments();

    expect((await screen.findAllByText('Spec da API v1')).length).toBeGreaterThan(0);
    expect(screen.queryByLabelText('Inconsistentes')).not.toBeInTheDocument();
    expect(screen.queryByText('Com dispensa registrada')).not.toBeInTheDocument();
    // E a palavra crua nunca chega ao dono.
    expect(document.body.textContent).not.toMatch(/waiver/i);
  });
});
