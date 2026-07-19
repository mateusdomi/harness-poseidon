import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';

import i18n from '@/i18n';
import { CommandPalette } from '@/app/components/command-palette';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

function RoutesLocation() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname}</output>;
}

function renderPalette() {
  return render(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/cockpit']}>
      <Routes>
        <Route
          path="*"
          element={
            <>
              <CommandPalette />
              <RoutesLocation />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
}

describe('CommandPalette', () => {
  beforeEach(async () => {
    await i18n.changeLanguage('pt-BR');
  });

  it('abre pelo botão de lupa e fecha com Esc', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.click(screen.getByRole('button', { name: /buscar telas/i }));
    expect(screen.getByRole('dialog', { name: 'Busca global de telas' })).toBeInTheDocument();
    // Foco gerenciado: o campo de busca recebe o foco ao abrir.
    expect(screen.getByRole('combobox', { name: 'Busca global de telas' })).toHaveFocus();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('abre pelo atalho de teclado (Ctrl+K)', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.keyboard('{Control>}k{/Control}');
    expect(screen.getByRole('dialog', { name: 'Busca global de telas' })).toBeInTheDocument();

    // O atalho alterna: um segundo Ctrl+K fecha.
    await user.keyboard('{Control>}k{/Control}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('abre pelo atalho com meta key (⌘K)', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.keyboard('{Meta>}k{/Meta}');
    expect(screen.getByRole('dialog', { name: 'Busca global de telas' })).toBeInTheDocument();
  });

  it('filtra por sinônimo/palavra-chave com normalização de acentos', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.click(screen.getByRole('button', { name: /buscar telas/i }));
    const input = screen.getByRole('combobox', { name: 'Busca global de telas' });

    // "kanban" é palavra-chave do Quadro (não aparece no nome da tela).
    await user.type(input, 'kanban');
    const listbox = screen.getByRole('listbox');
    expect(listbox).toHaveTextContent('Quadro');
    expect(listbox).not.toHaveTextContent('Cockpit');

    // Normalização: "auditoria" (sem acento) casa com a palavra-chave "auditoria" de Governança.
    await user.clear(input);
    await user.type(input, 'auditoria');
    expect(screen.getByRole('listbox')).toHaveTextContent('Governança');
  });

  it('agrupa os resultados por módulo (grupos do menu)', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.click(screen.getByRole('button', { name: /buscar telas/i }));
    await user.type(screen.getByRole('combobox'), 'projeto');

    const listbox = screen.getByRole('listbox');
    // "Projetos" (Operação) e "Executar projeto" (Orquestração) em grupos rotulados.
    expect(listbox).toHaveTextContent('Operação');
    expect(listbox).toHaveTextContent('Orquestração');
  });

  it('navega por teclado (setas) e seleciona com Enter', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.click(screen.getByRole('button', { name: /buscar telas/i }));
    await user.type(screen.getByRole('combobox'), 'agentes');

    // Dois resultados: "Agentes" (nome) primeiro, "Orquestrador" (palavra-chave) depois.
    const options = screen.getAllByRole('option');
    expect(options).toHaveLength(2);
    expect(options[0]).toHaveAttribute('aria-selected', 'true');
    expect(options[1]).toHaveAttribute('aria-selected', 'false');

    await user.keyboard('{ArrowDown}');
    expect(screen.getAllByRole('option')[1]).toHaveAttribute('aria-selected', 'true');

    // Seta para cima volta; wrap para baixo retorna ao primeiro.
    await user.keyboard('{ArrowUp}');
    expect(screen.getAllByRole('option')[0]).toHaveAttribute('aria-selected', 'true');

    await user.keyboard('{ArrowDown}');
    await user.keyboard('{Enter}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByTestId('location')).toHaveTextContent('/orchestrator');
  });

  it('navega ao clicar em uma opção', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.click(screen.getByRole('button', { name: /buscar telas/i }));
    await user.type(screen.getByRole('combobox'), 'govern');

    await user.click(screen.getByRole('option', { name: /governança/i }));
    expect(screen.getByTestId('location')).toHaveTextContent('/governance');
  });

  it('mostra estado vazio quando nada corresponde', async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.click(screen.getByRole('button', { name: /buscar telas/i }));
    await user.type(screen.getByRole('combobox'), 'xyzabc');

    expect(screen.getByText(/nenhuma tela encontrada/i)).toBeInTheDocument();
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });
});
