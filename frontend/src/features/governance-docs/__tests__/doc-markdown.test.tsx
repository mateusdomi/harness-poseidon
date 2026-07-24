import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import '@/i18n';
import { DocMarkdown } from '@/features/governance-docs/components/doc-markdown';

describe('DocMarkdown', () => {
  it('renderiza títulos, listas e ênfase como HTML formatado', () => {
    render(<DocMarkdown content={'# Título\n\n- item um\n- item dois\n\n**forte**'} />);

    expect(screen.getByRole('heading', { level: 1, name: 'Título' })).toBeInTheDocument();
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(screen.getByText('forte').tagName).toBe('STRONG');
  });

  it('renderiza tabelas GFM em uma <table> real (não texto cru)', () => {
    const md = ['| Coluna | Valor |', '| --- | --- |', '| a | 1 |', '| b | 2 |'].join('\n');
    render(<DocMarkdown content={md} />);

    expect(screen.getByRole('table')).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Coluna' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: '1' })).toBeInTheDocument();
  });

  it('destaca blocos de diagrama mermaid num bloco rotulado e legível', () => {
    const md = '```mermaid\ngraph TD;\n  A-->B;\n```';
    render(<DocMarkdown content={md} />);

    const figure = screen.getByRole('figure', { name: /Diagrama mermaid/i });
    expect(figure).toBeInTheDocument();
    // A fonte do diagrama continua legível dentro da figura.
    expect(figure.textContent).toContain('graph TD');
    expect(figure.textContent).toContain('A-->B');
  });

  it('renderiza blocos de código comuns dentro de <pre>', () => {
    render(<DocMarkdown content={'```ts\nconst x = 1;\n```'} />);
    const pre = document.querySelector('pre');
    expect(pre).not.toBeNull();
    expect(pre?.textContent).toContain('const x = 1;');
  });
});
