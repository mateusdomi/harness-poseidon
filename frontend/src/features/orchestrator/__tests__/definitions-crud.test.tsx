import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

import { createTestBundle, type TestBundle } from '@/api/__tests__/test-utils';
import OrchestratorPage from '@/features/orchestrator/pages/orchestrator-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderDefinitions(
  bundle: TestBundle = createTestBundle(),
  route = '/orchestrator?tab=definitions',
) {
  return renderWithApi(
    <MemoryRouter initialEntries={[route]}>
      <OrchestratorPage />
    </MemoryRouter>,
    bundle,
  );
}

/** Linha (<li>) da definição na lista — ignora <option> homônimo dos filtros. */
async function rowOf(name: string): Promise<HTMLElement> {
  const elements = await screen.findAllByText(name);
  const row = elements
    .map((element) => element.closest('li'))
    .find((element) => element !== null);
  if (!row) throw new Error(`Linha não encontrada: ${name}`);
  return row;
}

describe('Aba Definições de agentes', () => {
  it('lista todas as definições com estado, versão, time, modelo e provider', async () => {
    renderDefinitions();

    expect(await screen.findByText('Engenheiro Backend')).toBeInTheDocument();
    expect(screen.getByText('Engenheiro Frontend')).toBeInTheDocument();
    expect(screen.getByText('Revisor')).toBeInTheDocument();
    expect(screen.getByText('Testador')).toBeInTheDocument();
    expect(screen.getByText('Designer de Protótipos')).toBeInTheDocument();
    expect(screen.getByText('Analista de Segurança')).toBeInTheDocument();

    // Estado desabilitado + time Design (fixture).
    const designerRow = await rowOf('Designer de Protótipos');
    expect(within(designerRow).getByText('Desabilitada')).toBeInTheDocument();
    expect(within(designerRow).getByText('Design')).toBeInTheDocument();

    // Chefe: sem time, versão 2, modelo padrão e provider resolvidos.
    const chiefRow = await rowOf('Chefe');
    expect(within(chiefRow).getByText('Sem time')).toBeInTheDocument();
    expect(within(chiefRow).getByText('v2')).toBeInTheDocument();
    expect(within(chiefRow).getByText('GPT-4o')).toBeInTheDocument();
    expect(within(chiefRow).getByText('OpenAI')).toBeInTheDocument();
  });

  it('filtra por status: disabled mostra apenas o Designer de Protótipos', async () => {
    const user = userEvent.setup();
    renderDefinitions();

    await screen.findByText('Engenheiro Backend');
    await user.selectOptions(screen.getByLabelText('Status'), 'disabled');

    expect(screen.getByText('Designer de Protótipos')).toBeInTheDocument();
    expect(screen.queryByText('Engenheiro Backend')).not.toBeInTheDocument();
  });

  it('cria uma definição pelo formulário guiado, derivando a chave do nome', async () => {
    const user = userEvent.setup();
    renderDefinitions();

    await user.click(await screen.findByRole('button', { name: 'Nova definição' }));
    const dialog = await screen.findByRole('dialog', { name: 'Nova definição de agente' });

    // Passo 1 — identidade. A chave técnica é derivada do nome (§16.2).
    await user.type(within(dialog).getByLabelText(/Nome/), 'Analista de Dados');
    await user.click(within(dialog).getByRole('button', { name: 'Opções avançadas' }));
    expect(within(dialog).getByLabelText(/Identificador técnico|Chave/)).toHaveValue(
      'analista-de-dados',
    );

    // Avança até o passo de revisão e conclui.
    for (let step = 0; step < 5; step += 1) {
      await user.click(within(dialog).getByRole('button', { name: 'Avançar' }));
    }
    await user.click(within(dialog).getByRole('button', { name: 'Criar definição' }));

    await waitFor(() =>
      expect(
        screen.queryByRole('dialog', { name: 'Nova definição de agente' }),
      ).not.toBeInTheDocument(),
    );
    expect(await screen.findByText('Analista de Dados')).toBeInTheDocument();
  });

  it('edita uma definição e a versão incrementa', async () => {
    const user = userEvent.setup();
    renderDefinitions();

    const row = await rowOf('Revisor');
    expect(within(row).getByText('v1')).toBeInTheDocument();

    await user.click(within(row).getByRole('button', { name: 'Editar' }));
    const dialog = await screen.findByRole('dialog', { name: 'Editar definição — Revisor' });

    await user.clear(within(dialog).getByLabelText(/Nome/));
    await user.type(within(dialog).getByLabelText(/Nome/), 'Revisor Sênior');
    // Formulário guiado: avança até a revisão antes de salvar.
    for (let step = 0; step < 5; step += 1) {
      await user.click(within(dialog).getByRole('button', { name: 'Avançar' }));
    }
    await user.click(within(dialog).getByRole('button', { name: 'Salvar' }));

    await waitFor(() =>
      expect(
        screen.queryByRole('dialog', { name: 'Editar definição — Revisor' }),
      ).not.toBeInTheDocument(),
    );
    const updatedRow = await rowOf('Revisor Sênior');
    expect(within(updatedRow).getByText('v2')).toBeInTheDocument();
  });

  it('duplica uma definição criando a cópia com "(cópia)"', async () => {
    const user = userEvent.setup();
    renderDefinitions();

    const row = await rowOf('Analista de Segurança');
    await user.click(within(row).getByRole('button', { name: 'Duplicar' }));

    expect(await screen.findByText('Analista de Segurança (cópia)')).toBeInTheDocument();
  });

  it('exclui definição não utilizada (Analista de Segurança) com confirmação', async () => {
    const user = userEvent.setup();
    renderDefinitions();

    const row = await rowOf('Analista de Segurança');
    await user.click(within(row).getByRole('button', { name: 'Excluir' }));

    const dialog = await screen.findByRole('dialog', { name: 'Excluir definição' });
    await user.click(within(dialog).getByRole('button', { name: 'Excluir' }));

    await waitFor(() =>
      expect(screen.queryByText('Analista de Segurança')).not.toBeInTheDocument(),
    );
  });

  it('definição em uso não permite excluir e explica o motivo', async () => {
    renderDefinitions();

    const row = await rowOf('Engenheiro Backend');
    const deleteButton = within(row).getByRole('button', { name: 'Excluir' });
    expect(deleteButton).toBeDisabled();
    expect(deleteButton).toHaveAttribute('title', 'Definição em uso — utilize arquivar.');
  });

  it('arquiva uma definição com confirmação', async () => {
    const user = userEvent.setup();
    renderDefinitions();

    const row = await rowOf('Designer de Protótipos');
    await user.click(within(row).getByRole('button', { name: 'Arquivar' }));

    const dialog = await screen.findByRole('dialog', { name: 'Arquivar definição' });
    await user.click(within(dialog).getByRole('button', { name: 'Arquivar' }));

    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: 'Arquivar definição' })).not.toBeInTheDocument(),
    );
    const archivedRow = await rowOf('Designer de Protótipos');
    expect(await within(archivedRow).findByText('Arquivada')).toBeInTheDocument();
  });

  it('deep-link ?tab=definitions&definition=<id> abre a visualização da definição', async () => {
    const bundle = createTestBundle();
    const chief = bundle.fixtures.data['agent-definitions'].find(
      (definition) => definition.key === 'chief',
    )!;
    renderDefinitions(bundle, `/orchestrator?tab=definitions&definition=${chief.id}`);

    const dialog = await screen.findByRole('dialog', { name: 'Definição — Chefe' });
    // Versão atual + histórico de revisões (fixture do chefe tem 1 entrada na v2).
    expect(within(dialog).getAllByText('Versão 2').length).toBeGreaterThan(0);
    expect(within(dialog).getByText('Histórico de revisões')).toBeInTheDocument();
    expect(
      within(dialog).getByText('Campos alterados: fallbackModelIds, defaultEffort.'),
    ).toBeInTheDocument();
    // Reformatação de leitura (UX-AGENT-CARD): identidade humanizada da persona
    // no topo e responsabilidades quebradas em bullets legíveis (não texto
    // corrido separado por vírgula). Só apresentação — o conteúdo é o mesmo.
    expect(within(dialog).getByText('Bruna Magalhães')).toBeInTheDocument();
    expect(within(dialog).getByText('Perfil')).toBeInTheDocument();
    const responsibility = within(dialog).getByText('decompor demandas');
    expect(responsibility.tagName).toBe('LI');
  });
});
