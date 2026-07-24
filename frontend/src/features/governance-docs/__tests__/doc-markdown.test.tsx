import { StrictMode } from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import mermaid from 'mermaid';
import { describe, expect, it, vi } from 'vitest';

import '@/i18n';
import { DocMarkdown } from '@/features/governance-docs/components/doc-markdown';
import { sanitizeMermaidSvg } from '@/features/governance-docs/lib/mermaid-sanitize';

vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn().mockResolvedValue({
      svg: '<svg xmlns="http://www.w3.org/2000/svg"><text>Fluxo seguro</text></svg>',
    }),
  },
}));

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

  it('renderiza Mermaid localmente como SVG acessível', async () => {
    const md = '```mermaid\ngraph TD;\n  A-->B;\n```';
    render(<DocMarkdown content={md} />);

    const figure = screen.getByRole('figure', { name: /Diagrama mermaid/i });
    expect(figure).toBeInTheDocument();
    const diagram = await screen.findByRole('img', { name: /Visualização do diagrama Mermaid/i });
    expect(diagram.querySelector('svg')).not.toBeNull();
    expect(diagram.textContent).toContain('Fluxo seguro');
    await waitFor(() => expect(figure.textContent).not.toContain('graph TD'));
  });

  it('não reutiliza o ID temporário do Mermaid nos efeitos duplicados do StrictMode', async () => {
    const renderMock = vi.mocked(mermaid.render);
    const callsBefore = renderMock.mock.calls.length;
    render(
      <StrictMode>
        <DocMarkdown content={'```mermaid\ngraph TD;\n  A-->B;\n```'} />
      </StrictMode>,
    );

    await screen.findByRole('img', { name: /Visualização do diagrama Mermaid/i });
    const identifiers = renderMock.mock.calls
      .slice(callsBefore)
      .map(([identifier]) => identifier);
    expect(identifiers.length).toBeGreaterThan(1);
    expect(new Set(identifiers).size).toBe(identifiers.length);
  });

  it('mantém fallback de fonte explícito para PlantUML sem motor local', () => {
    render(<DocMarkdown content={'```plantuml\nAlice -> Bob: teste\n```'} />);

    const figure = screen.getByRole('figure', { name: /Diagrama plantuml/i });
    expect(figure.textContent).toContain('Diagrama (fonte)');
    expect(figure.textContent).toContain('Alice -> Bob');
  });

  it('sanitiza elementos executáveis e links externos no SVG do diagrama', () => {
    const svg = [
      '<svg xmlns="http://www.w3.org/2000/svg" onclick="alert(1)">',
      '<script>alert(1)</script>',
      '<a href="https://example.invalid"><text>externo</text></a>',
      '<use href="#local"/>',
      '</svg>',
    ].join('');

    const sanitized = sanitizeMermaidSvg(svg);

    expect(sanitized).not.toContain('onclick');
    expect(sanitized).not.toContain('<script');
    expect(sanitized).not.toContain('https://example.invalid');
    expect(sanitized).toContain('href="#local"');
  });

  it('renderiza blocos de código comuns dentro de <pre>', () => {
    render(<DocMarkdown content={'```ts\nconst x = 1;\n```'} />);
    const pre = document.querySelector('pre');
    expect(pre).not.toBeNull();
    expect(pre?.textContent).toContain('const x = 1;');
  });
});
