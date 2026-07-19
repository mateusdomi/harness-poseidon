import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { COMPONENT_ROUTER_FUTURE_FLAGS } from '@/app/router-future';

import { createTestBundle } from '@/api/__tests__/test-utils';
import {
  classifyRunLogLine,
  extractServiceName,
  filterRunLogEntries,
  type RunLogEntry,
} from '@/features/run-project/lib/run-project-derive';
import RunProjectPage from '@/features/run-project/pages/run-project-page';
import { renderWithApi } from '@/test/render-with-providers';

function renderPage() {
  const bundle = createTestBundle();
  return renderWithApi(
    <MemoryRouter future={COMPONENT_ROUTER_FUTURE_FLAGS} initialEntries={['/run-project']}>
      <Routes>
        <Route path="/run-project" element={<RunProjectPage />} />
      </Routes>
    </MemoryRouter>,
    bundle,
  );
}

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

  it('credenciais demo ficam mascaradas até revelar', async () => {
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByText('Credenciais de demonstração')).toBeInTheDocument();
    expect(screen.queryByText('demo@poseidon.local')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Revelar' }));
    expect(screen.getByText('demo@poseidon.local')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Ocultar' }));
    expect(screen.queryByText('demo@poseidon.local')).not.toBeInTheDocument();
  });
});
