import { useState } from 'react';
import { act, render, renderHook, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useSearchParams } from 'react-router-dom';

import '@/i18n';
import { PaginationBar } from '@/features/shared/components/pagination';
import {
  DEFAULT_PAGE_SIZE,
  usePagination,
  useUrlPagination,
} from '@/features/shared/hooks/use-pagination';

const items = Array.from({ length: 42 }, (_, index) => `item-${index + 1}`);

describe('usePagination', () => {
  it('fatia a coleção com 15 itens por página por padrão', () => {
    const { result } = renderHook(() => usePagination(items.length));

    expect(result.current.pageSize).toBe(DEFAULT_PAGE_SIZE);
    expect(result.current.pageCount).toBe(3);
    expect(result.current.paginate(items)).toHaveLength(15);
    expect(result.current.paginate(items)[0]).toBe('item-1');
    expect(result.current.rangeStart).toBe(1);
    expect(result.current.rangeEnd).toBe(15);
  });

  it('navega entre páginas e clampa nos limites', () => {
    const { result } = renderHook(() => usePagination(items.length, { pageSize: 30 }));

    act(() => result.current.setPage(2));
    expect(result.current.page).toBe(2);
    expect(result.current.paginate(items)).toHaveLength(12);
    expect(result.current.rangeStart).toBe(31);
    expect(result.current.rangeEnd).toBe(42);

    act(() => result.current.setPage(99));
    expect(result.current.page).toBe(2);
    act(() => result.current.setPage(0));
    expect(result.current.page).toBe(1);
  });

  it('volta para a página 1 ao trocar o tamanho da página', () => {
    const { result } = renderHook(() => usePagination(items.length));

    act(() => result.current.setPage(3));
    expect(result.current.page).toBe(3);
    act(() => result.current.setPageSize(50));
    expect(result.current.page).toBe(1);
    expect(result.current.pageSize).toBe(50);
    expect(result.current.pageCount).toBe(1);
  });

  it('reseta para a página 1 quando o resetKey muda (filtro/busca)', () => {
    const { result, rerender } = renderHook(
      ({ resetKey }) => usePagination(items.length, { resetKey }),
      { initialProps: { resetKey: 'filtro-a' } },
    );

    act(() => result.current.setPage(2));
    expect(result.current.page).toBe(2);

    rerender({ resetKey: 'filtro-b' });
    expect(result.current.page).toBe(1);
  });

  it('clampa a página quando o total encolhe', () => {
    const { result, rerender } = renderHook(({ total }) => usePagination(total), {
      initialProps: { total: 42 },
    });

    act(() => result.current.setPage(3));
    expect(result.current.page).toBe(3);

    rerender({ total: 10 });
    expect(result.current.page).toBe(1);
    expect(result.current.rangeStart).toBe(1);
    expect(result.current.rangeEnd).toBe(10);
  });

  it('sincroniza page/pageSize na URL preservando params existentes', () => {
    let currentSearch = '';
    function SearchProbe() {
      const [params] = useSearchParams();
      currentSearch = params.toString();
      return null;
    }
    const wrapper = ({ children }: { children: React.ReactNode }) => (
      <MemoryRouter initialEntries={['/documentos?doc=doc-1']}>
        <SearchProbe />
        {children}
      </MemoryRouter>
    );
    const { result } = renderHook(() => useUrlPagination(items.length), {
      wrapper,
    });

    expect(currentSearch).toBe('doc=doc-1');

    act(() => result.current.setPage(2));
    expect(currentSearch).toContain('doc=doc-1');
    expect(currentSearch).toContain('page=2');

    act(() => result.current.setPageSize(30));
    // Trocar o tamanho volta para a página 1 e o param some (padrão omitido).
    expect(result.current.page).toBe(1);
    expect(currentSearch).toContain('pageSize=30');
    expect(currentSearch).not.toContain('page=1');

    act(() => result.current.setPage(2));
    expect(result.current.paginate(items)[0]).toBe('item-31');
  });
});

describe('PaginationBar', () => {
  function renderBar(total: number) {
    function Harness() {
      const pagination = usePagination(total);
      return <PaginationBar pagination={pagination} />;
    }
    return render(<Harness />);
  }

  it('não renderiza quando a coleção cabe em uma página', () => {
    renderBar(15);
    expect(screen.queryByRole('navigation', { name: 'Paginação' })).not.toBeInTheDocument();
  });

  it('exibe intervalo, total e controles acessíveis', async () => {
    const user = userEvent.setup();
    renderBar(42);

    const nav = screen.getByRole('navigation', { name: 'Paginação' });
    expect(nav).toBeInTheDocument();
    expect(screen.getByText('1–15 de 42')).toBeInTheDocument();

    const previous = screen.getByRole('button', { name: 'Página anterior' });
    expect(previous).toBeDisabled();

    await user.click(screen.getByRole('button', { name: 'Próxima página' }));
    expect(screen.getByText('16–30 de 42')).toBeInTheDocument();
    expect(previous).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Página 2', current: 'page' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Página 3' }));
    expect(screen.getByText('31–42 de 42')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Próxima página' })).toBeDisabled();
  });

  it('troca o tamanho da página pelo seletor', async () => {
    const user = userEvent.setup();
    renderBar(42);

    await user.selectOptions(screen.getByLabelText('Itens por página'), '50');
    expect(screen.getByText('1–42 de 42')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Próxima página' })).toBeDisabled();
  });

  it('reseta para a página 1 ao mudar o filtro (integração hook + barra)', async () => {
    const user = userEvent.setup();

    function Harness() {
      const [filter, setFilter] = useState('todos');
      const pagination = usePagination(42, { resetKey: filter });
      return (
        <>
          <button type="button" onClick={() => setFilter('ativos')}>
            filtrar
          </button>
          <PaginationBar pagination={pagination} />
        </>
      );
    }
    render(<Harness />);

    await user.click(screen.getByRole('button', { name: 'Página 2' }));
    expect(screen.getByText('16–30 de 42')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'filtrar' }));
    expect(screen.getByText('1–15 de 42')).toBeInTheDocument();
  });
});
