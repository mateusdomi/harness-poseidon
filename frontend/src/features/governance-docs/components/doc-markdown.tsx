import { isValidElement, type ReactNode } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { useTranslation } from 'react-i18next';

import { cn } from '@/lib/utils';

/**
 * Render legível de documentos de governança (.md). Reúso do react-markdown já
 * presente no projeto (ver MarkdownContent / D-016, D-027), estendido com GFM
 * (tabelas, listas de tarefas, strikethrough) — pois o MarkdownContent do chat
 * não renderiza tabelas — e com um bloco dedicado a diagramas.
 *
 * Diagramas (```mermaid```, C4/PlantUML): o bundle não embarca um renderizador
 * de diagramas (sem rede externa/CSP), então formatamos a fonte num bloco
 * legível e rotulado. Se um render nativo (mermaid) for adicionado ao bundle no
 * futuro, basta trocar o corpo de DiagramBlock.
 */

const DIAGRAM_LANGS = new Set([
  'mermaid',
  'c4',
  'c4plantuml',
  'plantuml',
  'puml',
  'graphviz',
  'dot',
]);

function extractLang(className?: string): string | null {
  if (!className) return null;
  const match = /language-([\w-]+)/.exec(className);
  return match ? match[1].toLowerCase() : null;
}

/** Concatena o texto cru de um nó React (string | número | árvore de filhos). */
function nodeText(node: ReactNode): string {
  if (node == null || node === false) return '';
  if (typeof node === 'string' || typeof node === 'number') return String(node);
  if (Array.isArray(node)) return node.map(nodeText).join('');
  if (isValidElement<{ children?: ReactNode }>(node)) return nodeText(node.props.children);
  return '';
}

export function DocMarkdown({ content }: { content: string }) {
  const { t } = useTranslation();

  return (
    <div
      className={cn(
        'doc-markdown flex flex-col gap-3 text-sm leading-relaxed text-foreground',
        '[&_h1]:font-heading [&_h1]:text-xl [&_h1]:font-semibold',
        '[&_h2]:font-heading [&_h2]:text-lg [&_h2]:font-semibold [&_h2]:mt-2',
        '[&_h3]:font-heading [&_h3]:text-base [&_h3]:font-semibold',
        '[&_h4]:font-heading [&_h4]:text-sm [&_h4]:font-semibold',
        '[&_ol]:list-decimal [&_ol]:pl-5 [&_ul]:list-disc [&_ul]:pl-5 [&_li]:my-0.5',
        '[&_blockquote]:border-l-2 [&_blockquote]:border-border [&_blockquote]:pl-3 [&_blockquote]:text-foreground-muted',
        '[&_strong]:font-semibold [&_hr]:border-border',
        '[&_input[type=checkbox]]:mr-1',
      )}
    >
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          a: ({ children, href }) => (
            <a
              href={href}
              target="_blank"
              rel="noreferrer"
              className="text-brand-strong underline underline-offset-2"
            >
              {children}
            </a>
          ),
          table: ({ children }) => (
            <div className="overflow-x-auto">
              <table className="w-full border-collapse text-xs">{children}</table>
            </div>
          ),
          thead: ({ children }) => <thead className="bg-surface-elevated">{children}</thead>,
          th: ({ children, style }) => (
            <th
              style={style}
              className="border border-border px-2 py-1 text-left font-semibold"
            >
              {children}
            </th>
          ),
          td: ({ children, style }) => (
            <td style={style} className="border border-border px-2 py-1 align-top">
              {children}
            </td>
          ),
          pre: ({ children }) => {
            // O filho é o elemento <code> da cerca; extrai a linguagem para
            // decidir entre bloco de código comum e bloco de diagrama.
            const codeEl = Array.isArray(children) ? children[0] : children;
            const className = isValidElement<{ className?: string }>(codeEl)
              ? codeEl.props.className
              : undefined;
            const lang = extractLang(className);

            if (lang && DIAGRAM_LANGS.has(lang)) {
              const source = isValidElement<{ children?: ReactNode }>(codeEl)
                ? nodeText(codeEl.props.children)
                : '';
              return (
                <figure
                  className="rounded-md border border-border bg-surface-elevated"
                  aria-label={t('governanceDocs.viewer.diagram', { lang })}
                >
                  <figcaption className="flex items-center gap-2 border-b border-border px-3 py-1.5 text-xs font-medium text-foreground-muted">
                    <span className="rounded bg-surface px-1.5 py-0.5 font-mono uppercase text-brand-strong">
                      {lang}
                    </span>
                    {t('governanceDocs.viewer.diagramLabel')}
                  </figcaption>
                  <pre className="overflow-x-auto p-3 font-mono text-xs leading-relaxed">
                    {source.replace(/\n$/, '')}
                  </pre>
                </figure>
              );
            }

            return (
              <pre className="overflow-x-auto rounded-md border border-border bg-surface-elevated p-3 font-mono text-xs">
                {children}
              </pre>
            );
          },
          code: ({ children, className }) =>
            className ? (
              <code className={cn('font-mono text-xs', className)}>{children}</code>
            ) : (
              <code className="rounded bg-surface-elevated px-1 py-0.5 font-mono text-xs">
                {children}
              </code>
            ),
        }}
      >
        {content}
      </ReactMarkdown>
    </div>
  );
}
