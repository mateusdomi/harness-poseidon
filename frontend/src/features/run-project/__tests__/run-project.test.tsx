import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  classifyRunLogLine,
  extractServiceName,
  filterRunLogEntries,
  type RunLogEntry,
} from '@/features/run-project/lib/run-project-derive';
import RunProjectPage from '@/features/run-project/pages/run-project-page';
import { renderWithApi } from '@/test/render-with-providers';
import { useActiveProjectStore } from '@/stores/active-project-store';
import { usePresentationStore } from '@/stores/presentation-store';
import { useSessionStore } from '@/stores/session-store';

/**
 * O ambiente completo — serviços, portas, pilha, logs, limpeza — é leitura
 * TÉCNICA desde a F8/D8. O modo Negócio tem um botão e um endereço.
 */
function renderPage(mode: 'business' | 'technical' = 'technical') {
  const bundle = createTestBundle();
  const profileId = bundle.fixtures.meta.currentProfileId;
  useSessionStore.setState({ activeProfileId: profileId });
  usePresentationStore.getState().requestMode(profileId, mode);
  return renderWithApi(
    <MemoryRouter initialEntries={['/run-project']}>
      <Routes>
        <Route path="/run-project" element={<RunProjectPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

beforeEach(() => {
  usePresentationStore.setState({ modeByProfile: {} });
  useActiveProjectStore.setState({ selectionsByProfile: {} });
  useSessionStore.setState({ activeProfileId: null });
});

function entry(seq: number, line: string): RunLogEntry {
  return { seq, line, occurredAt: '2026-07-17T12:00:00Z' };
}

describe('run-project-derive', () => {
  it('classifica o nível da linha pelo conteúdo', () => {
    expect(classifyRunLogLine('Iniciando "API" na porta 5001…')).toBe('info');
    expect(classifyRunLogLine('"API" pronto — health check OK em porta 5001.')).toBe('info');
    expect(classifyRunLogLine('Encerrando "API"…')).toBe('warning');
    expect(classifyRunLogLine('Cleanup: "API" parado.')).toBe('warning');
    expect(classifyRunLogLine('"API" falhou ao subir: erro de porta ocupada')).toBe('error');
  });

  it('extrai o nome do serviço citado entre aspas', () => {
    expect(extractServiceName('Iniciando "Frontend Vite (dev)" na porta 5173…')).toBe(
      'Frontend Vite (dev)',
    );
    expect(extractServiceName('Cleanup do ambiente concluído — 2 serviço(s) parado(s).')).toBeNull();
  });

  it('filtra por nível, serviço e texto', () => {
    const entries = [
      entry(1, 'Iniciando "API" na porta 5001…'),
      entry(2, '"API" parado.'),
      entry(3, 'Iniciando "Web" na porta 5173…'),
    ];
    expect(filterRunLogEntries(entries, { level: 'warning', service: '', text: '' })).toHaveLength(1);
    expect(filterRunLogEntries(entries, { level: '', service: 'Web', text: '' })).toHaveLength(1);
    expect(filterRunLogEntries(entries, { level: '', service: '', text: 'porta 5001' })).toHaveLength(1);
    expect(filterRunLogEntries(entries, { level: '', service: '', text: '' })).toHaveLength(3);
  });
});

describe('RunProjectPage', () => {
  it('lista serviços e inicia um serviço com logs em streaming', async () => {
    const user = userEvent.setup();
    const { bundle } = renderPage();

    // Nome aparece no card e no filtro de serviço do painel de logs.
    expect((await screen.findAllByText('Frontend Vite (dev)')).length).toBeGreaterThan(0);
    expect(screen.getAllByText('Backend API (.NET)').length).toBeGreaterThan(0);

    // O backend está parado na fixture: o botão Iniciar dele está habilitado.
    const card = screen.getAllByText('Backend API (.NET)')[0].closest('li')!;
    const startButton = Array.from(card.querySelectorAll('button')).find(
      (button) => button.textContent === 'Iniciar',
    )!;
    await user.click(startButton);

    // Estado muda no mock e o log chega pelo stream do projeto.
    await screen.findByText(/Iniciando "Backend API \(\.NET\)"/, undefined, { timeout: 3000 });
    const target = bundle.fixtures.data['run-targets'].find((t) => t.name === 'Backend API (.NET)')!;
    await waitFor(async () => {
      expect((await bundle.api.get('run-targets', target.id)).state).toBe('running');
    });
  });

  it('não revela credenciais que não vieram do backend autorizado', async () => {
    renderPage('technical');

    expect(await screen.findByText('Credenciais de demonstração')).toBeInTheDocument();
    expect(screen.queryByText('demo@poseidon.local')).not.toBeInTheDocument();
    expect(
      screen.getByText(/Nenhuma credencial de demonstração foi fornecida para este projeto/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revelar' })).not.toBeInTheDocument();
  });

  it('card de modo local exibe estado do ambiente, diretório de dados e link de diagnóstico', async () => {
    renderPage('technical');

    expect(await screen.findByText('Modo local')).toBeInTheDocument();
    // Host/frontend observado + Frontend Vite (running) + Backend API (stopped).
    expect(screen.getByText('2 de 3 serviço(s) em execução')).toBeInTheDocument();
    // Diretório de trabalho vem das settings; dados locais têm caminho próprio.
    expect(screen.getByText('~/poseidon')).toBeInTheDocument();
    expect(screen.getByText('~/.harness-poseidon')).toBeInTheDocument();
    // Sem âncora na seção de diagnóstico de /settings: link simples para a página.
    expect(screen.getByRole('link', { name: 'Verificação do ambiente' })).toHaveAttribute('href', '/settings');
    // Instrução estática de atalho — texto informativo, sem botão.
    expect(screen.getByText(/Instrução: para abrir este ambiente fora do app/)).toBeInTheDocument();
  });

  /* ---- F8/D8: "Abrir <projeto>" no modo Negócio ---- */

  it('modo Negócio mostra um botão e o endereço do produto, sem o ambiente', async () => {
    renderPage('business');

    // A fixture tem o front marcado como a tela do cliente e já em execução.
    expect(
      await screen.findByRole('link', { name: 'Abrir Poseidon Frontend' }),
    ).toHaveAttribute('href', 'http://localhost:5173');
    expect(screen.getByText('No ar')).toBeInTheDocument();

    // O resto do ambiente não é assunto do dono: nem serviço interno, nem porta,
    // nem pilha, nem logs, nem limpeza de ambiente.
    expect(screen.queryByText('Backend API (.NET)')).not.toBeInTheDocument();
    expect(screen.queryByText('Modo local')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Limpar ambiente' })).not.toBeInTheDocument();
    expect(screen.queryByText('5001')).not.toBeInTheDocument();
  });

  it('modo Negócio não elege serviço quando nenhum está marcado como tela do cliente', async () => {
    const bundle = createTestBundle();
    const profileId = bundle.fixtures.meta.currentProfileId;
    useSessionStore.setState({ activeProfileId: profileId });
    usePresentationStore.getState().requestMode(profileId, 'business');
    // Projeto cuja fixture só tem serviço interno (worker), nenhum voltado ao usuário.
    const semTela = bundle.fixtures.data.projects[1];
    useActiveProjectStore.getState().selectProject(profileId, semTela.id);

    renderWithApi(
      <MemoryRouter initialEntries={['/run-project']}>
        <Routes>
          <Route path="/run-project" element={<RunProjectPage />} />
        </Routes>
      </MemoryRouter>,
      bundle,
    );

    expect(
      await screen.findByText(/Ainda não sabemos qual serviço deste projeto entrega a tela/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /^Abrir/ })).not.toBeInTheDocument();
  });
});
