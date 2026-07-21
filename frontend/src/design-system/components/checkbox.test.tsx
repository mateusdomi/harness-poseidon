import { useState } from 'react';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { Checkbox } from './checkbox';

/**
 * O check e o traço são elementos SVG reais no DOM (lucide), revelados por
 * classe (`peer-checked` / `peer-data-[indeterminate]`). O ambiente de teste
 * roda com `css: false`, então a visibilidade computada (opacity) é validada
 * no E2E real; aqui garantimos a PRESENÇA do indicador e o wiring das classes
 * que o revelam — nunca apenas `checked=true`.
 */
function getCheckIcon(container: HTMLElement) {
  return container.querySelector('.lucide-check');
}
function getDashIcon(container: HTMLElement) {
  return container.querySelector('.lucide-minus');
}

describe('Checkbox (design system)', () => {
  it('desmarcado: é um checkbox real e o indicador de check está oculto por classe', () => {
    const { container } = render(<Checkbox aria-label="aceitar" checked={false} readOnly />);
    const box = screen.getByRole('checkbox', { name: 'aceitar' });
    expect(box).toBeInTheDocument();
    expect(box).not.toBeChecked();
    // O indicador existe no DOM e é revelado apenas quando marcado.
    const check = getCheckIcon(container);
    expect(check).not.toBeNull();
    expect(check).toHaveClass('opacity-0', 'peer-checked:opacity-100');
  });

  it('marcado: além de checked, há preenchimento (fill) e ícone de check presente', () => {
    const { container } = render(<Checkbox aria-label="aceitar" checked readOnly />);
    const box = screen.getByRole('checkbox', { name: 'aceitar' });
    expect(box).toBeChecked();
    // Preenchimento: não depende só de cor de borda.
    expect(box).toHaveClass('checked:bg-accent', 'checked:border-accent');
    // O check está presente e mapeado ao estado marcado (indicador, não só cor).
    expect(getCheckIcon(container)).not.toBeNull();
  });

  it('indeterminate: reflete a propriedade nativa no DOM e mostra o traço', () => {
    const { container } = render(<Checkbox aria-label="todos" indeterminate readOnly />);
    const box = screen.getByRole('checkbox', { name: 'todos' }) as HTMLInputElement;
    expect(box.indeterminate).toBe(true);
    expect(box).toHaveAttribute('data-indeterminate');
    const dash = getDashIcon(container);
    expect(dash).not.toBeNull();
    expect(dash).toHaveClass('peer-data-[indeterminate]:opacity-100');
  });

  it('alterna indeterminate → não-indeterminate atualizando a propriedade do DOM', () => {
    const { rerender } = render(<Checkbox aria-label="todos" indeterminate readOnly />);
    const box = screen.getByRole('checkbox', { name: 'todos' }) as HTMLInputElement;
    expect(box.indeterminate).toBe(true);
    rerender(<Checkbox aria-label="todos" indeterminate={false} readOnly />);
    expect(box.indeterminate).toBe(false);
    expect(box).not.toHaveAttribute('data-indeterminate');
  });

  it('disabled marcado e desmarcado', () => {
    const { rerender } = render(<Checkbox aria-label="x" disabled checked readOnly />);
    const box = screen.getByRole('checkbox', { name: 'x' });
    expect(box).toBeDisabled();
    expect(box).toBeChecked();
    expect(box).toHaveClass('disabled:opacity-50', 'disabled:cursor-not-allowed');
    rerender(<Checkbox aria-label="x" disabled checked={false} readOnly />);
    expect(box).toBeDisabled();
    expect(box).not.toBeChecked();
  });

  it('teclado: Space marca/desmarca e dispara onChange', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    function Controlled() {
      const [checked, setChecked] = useState(false);
      return (
        <Checkbox
          aria-label="via teclado"
          checked={checked}
          onChange={(e) => {
            onChange(e.target.checked);
            setChecked(e.target.checked);
          }}
        />
      );
    }
    render(<Controlled />);
    const box = screen.getByRole('checkbox', { name: 'via teclado' });
    await user.tab();
    expect(box).toHaveFocus();
    await user.keyboard(' ');
    expect(onChange).toHaveBeenLastCalledWith(true);
    expect(box).toBeChecked();
    await user.keyboard(' ');
    expect(onChange).toHaveBeenLastCalledWith(false);
    expect(box).not.toBeChecked();
  });

  it('label clicável: clicar no texto marca o controle', async () => {
    const user = userEvent.setup();
    function Controlled() {
      const [checked, setChecked] = useState(false);
      return (
        <label>
          <Checkbox checked={checked} onChange={(e) => setChecked(e.target.checked)} />
          Notificações ativadas
        </label>
      );
    }
    render(<Controlled />);
    const box = screen.getByRole('checkbox', { name: 'Notificações ativadas' });
    expect(box).not.toBeChecked();
    await user.click(screen.getByText('Notificações ativadas'));
    expect(box).toBeChecked();
  });

  it('foco visível: aplica o anel de foco e é focável por teclado', async () => {
    const user = userEvent.setup();
    render(<Checkbox aria-label="foco" />);
    const box = screen.getByRole('checkbox', { name: 'foco' });
    expect(box).toHaveClass('focus-visible:ring-2', 'focus-visible:ring-brand');
    await user.tab();
    expect(box).toHaveFocus();
  });

  it('inválido: aria-invalid marca a borda de erro sem depender só de cor no campo', () => {
    render(<Checkbox aria-label="obrigatório" aria-invalid readOnly />);
    const box = screen.getByRole('checkbox', { name: 'obrigatório' });
    expect(box).toHaveAttribute('aria-invalid', 'true');
    expect(box).toHaveClass('aria-[invalid=true]:border-error');
  });

  it('encaminha a ref para o input real', () => {
    const ref = { current: null as HTMLInputElement | null };
    render(<Checkbox aria-label="ref" ref={ref} readOnly checked={false} />);
    expect(ref.current).toBeInstanceOf(HTMLInputElement);
    expect(ref.current?.type).toBe('checkbox');
  });
});
