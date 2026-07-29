import { act, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { streams } from '@/api';
import { createTestBundle } from '@/api/__tests__/test-utils';
import ToolsPage from '@/features/tools/pages/tools-page';
import {
  nextComponentState,
  providedToolNames,
  sortByName,
  toggleAction,
  toolOrigin,
} from '@/features/tools/lib/tools-derive';
import { renderWithApi } from '@/test/render-with-providers';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

const fixtures = createTestBundle().fixtures.data;

function renderTools(mode: 'business' | 'technical' = 'technical') {
  // Bundle novo por teste: o store do mock é mutável (estado dos itens).
  const bundle = createTestBundle();
  useSessionStore.setState({ activeProfileId: bundle.fixtures.meta.currentProfileId });
  usePresentationStore.setState({ modeByProfile: {} });
  usePresentationStore.getState().requestMode(bundle.fixtures.meta.currentProfileId, mode);
  return renderWithApi(<ToolsPage />, bundle);
}

describe('tools-derive', () => {
  it('alterna o estado: enabled oferece desabilitar; disabled/error oferecem habilitar', () => {
    expect(toggleAction('enabled')).toBe('disable');
    expect(nextComponentState('enabled')).toBe('disabled');
    expect(toggleAction('disabled')).toBe('enable');
    expect(nextComponentState('disabled')).toBe('enabled');
    expect(toggleAction('error')).toBe('enable');
    expect(nextComponentState('error')).toBe('enabled');
  });

  it('deriva a origem da ferramenta: interna, plugin provedor ou MCP', () => {
    const terminal = fixtures.tools.find((tool) => tool.key === 'shell')!;
    expect(toolOrigin(terminal, fixtures.plugins)).toEqual({ type: 'builtin' });

    const figma = fixtures.tools.find((tool) => tool.key === 'figma.export')!;
    const ponte = fixtures.plugins.find((plugin) => plugin.key === 'figma-bridge')!;
    expect(toolOrigin(figma, fixtures.plugins)).toEqual({ type: 'plugin', plugin: ponte });

    const githubPr = fixtures.tools.find((tool) => tool.key === 'github.pr')!;
    // O contrato não liga ferramenta ↔ servidor MCP: só o tipo é conhecido.
    expect(toolOrigin(githubPr, fixtures.plugins)).toEqual({ type: 'mcp' });
  });

  it('resolve os nomes das ferramentas providas por um plugin', () => {
    const ponte = fixtures.plugins.find((plugin) => plugin.key === 'figma-bridge')!;
    expect(providedToolNames(ponte, fixtures.tools)).toEqual(['Exportar Figma']);
  });

  it('ordena por nome sem mutar a entrada', () => {
    const input = [...fixtures.skills];
    const sorted = sortByName(input);
    expect(sorted.map((skill) => skill.name)).toEqual(
      [...input].map((skill) => skill.name).sort((a, b) => a.localeCompare(b, 'pt-BR')),
    );
    expect(input.map((skill) => skill.id)).toEqual(fixtures.skills.map((skill) => skill.id));
  });
});

describe('ToolsPage', () => {
  it('reserva o catálogo ao modo Técnico', () => {
    renderTools('business');

    expect(screen.getByText('Área disponível no modo Técnico')).toBeInTheDocument();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
  });

  it('lista ferramentas na aba inicial com estado, kind e origem', async () => {
    renderTools();

    expect(screen.getByText('Como registrar um novo item')).toBeInTheDocument();
    expect(screen.getByText(/Não há cadastro manual nesta tela/)).toBeInTheDocument();
    const tablist = await screen.findByRole('tablist', { name: 'Categorias do catálogo' });
    expect(within(tablist).getAllByRole('tab')).toHaveLength(4);

    const list = await screen.findByRole('list', { name: 'Lista de ferramentas' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(6);

    // Ferramenta interna: kind "Interna" + origem "Interna".
    const terminal = items.find((item) => within(item).queryByText('Terminal') !== null)!;
    expect(within(terminal).getByText('Habilitado')).toBeInTheDocument();
    expect(within(terminal).getByText('Origem: Interna')).toBeInTheDocument();

    // Ferramenta de plugin em erro: origem = plugin que a provê.
    const figma = items.find((item) => within(item).queryByText('Exportar Figma') !== null)!;
    expect(within(figma).getByText('Com erro')).toBeInTheDocument();
    expect(within(figma).getByText('Origem: Plugin Ponte Figma')).toBeInTheDocument();
  });

  it('mostra skills com versão e plugins com ferramentas providas', async () => {
    const user = userEvent.setup();
    renderTools();

    await user.click(await screen.findByRole('tab', { name: /Skills/ }));
    const skillList = await screen.findByRole('list', { name: 'Lista de skills' });
    const prototipacao = within(skillList)
      .getAllByRole('listitem')
      .find((item) => within(item).queryByText('Prototipação') !== null)!;
    expect(within(prototipacao).getByText('Desabilitado')).toBeInTheDocument();
    expect(within(prototipacao).getByText('Versão 0.4.0')).toBeInTheDocument();

    await user.click(screen.getByRole('tab', { name: /Plugins/ }));
    const pluginList = await screen.findByRole('list', { name: 'Lista de plugins' });
    const ponte = within(pluginList)
      .getAllByRole('listitem')
      .find((item) => within(item).queryByText('Ponte Figma') !== null)!;
    expect(within(ponte).getByText('Ferramentas providas: Exportar Figma')).toBeInTheDocument();
  });

  it('mostra servidores MCP com transport, toolCount e endpoint mascarado', async () => {
    const user = userEvent.setup();
    renderTools();

    await user.click(await screen.findByRole('tab', { name: /Servidores MCP/ }));
    const list = await screen.findByRole('list', { name: 'Lista de servidores MCP' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(2);

    const github = items.find((item) => within(item).queryByText('github-mcp') !== null)!;
    expect(within(github).getByText('HTTP')).toBeInTheDocument();
    expect(within(github).getByText('6 ferramentas')).toBeInTheDocument();
    expect(within(github).getByText('Revelado somente com permissão.')).toBeInTheDocument();
    // Endpoint exibido sem credenciais (fixture não tem; a máscara é aplicada sempre).
    expect(within(github).getByText('https://mcp.github.local/sse')).toBeInTheDocument();
  });

  it('desabilita uma ferramenta com confirmação no dialog', async () => {
    const user = userEvent.setup();
    renderTools();

    const list = await screen.findByRole('list', { name: 'Lista de ferramentas' });
    const terminal = within(list)
      .getAllByRole('listitem')
      .find((item) => within(item).queryByText('Terminal') !== null)!;

    await user.click(within(terminal).getByRole('button', { name: 'Desabilitar' }));
    const dialog = await screen.findByRole('dialog', { name: 'Desabilitar Terminal' });
    expect(
      within(dialog).getByText(
        'O item ficará indisponível para os agentes até ser habilitado novamente.',
      ),
    ).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Desabilitar' }));
    expect(await within(terminal).findByText('Desabilitado')).toBeInTheDocument();
    expect(within(terminal).getByRole('button', { name: 'Habilitar' })).toBeInTheDocument();
  });

  it('avisa no dialog quando o item está em erro (e permite habilitar)', async () => {
    const user = userEvent.setup();
    renderTools();

    const list = await screen.findByRole('list', { name: 'Lista de ferramentas' });
    const figma = within(list)
      .getAllByRole('listitem')
      .find((item) => within(item).queryByText('Exportar Figma') !== null)!;

    await user.click(within(figma).getByRole('button', { name: 'Habilitar' }));
    const dialog = await screen.findByRole('dialog', { name: 'Habilitar Exportar Figma' });
    expect(within(dialog).getByText(/Este item está em estado de erro/)).toBeInTheDocument();

    await user.click(within(dialog).getByRole('button', { name: 'Habilitar' }));
    expect(await within(figma).findByText('Habilitado')).toBeInTheDocument();
  });

  it('tool.statusChanged no stream global invalida o catálogo', async () => {
    const { bundle, queryClient } = renderTools();
    await screen.findByRole('list', { name: 'Lista de ferramentas' });

    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    const toolId = bundle.fixtures.data.tools[0].id;
    act(() => {
      bundle.realtime.emit(streams.global(), 'tool.statusChanged', {
        toolId,
        from: 'enabled',
        to: 'error',
      });
    });

    await waitFor(() => {
      expect(
        invalidateSpy.mock.calls.some((call) => JSON.stringify(call[0]).includes('"catalog"')),
      ).toBe(true);
    });
  });
});
