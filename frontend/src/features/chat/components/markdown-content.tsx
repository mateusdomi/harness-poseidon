import ReactMarkdown from 'react-markdown';

import { cn } from '@/lib/utils';

/**
 * Markdown das mensagens com destaque de código via tokens do DS
 * (bloco: superfície elevada + mono; inline: chip mono). Links abrem em
 * nova aba. Sem syntax highlighter pesado — ver DECISIONS (D-016).
 */
export function MarkdownContent({ content }: { content: string }) {
  return (
    <div className="flex flex-col gap-2 text-sm leading-relaxed [&_ol]:list-decimal [&_ol]:pl-5 [&_ul]:list-disc [&_ul]:pl-5 [&_h1]:font-heading [&_h1]:text-base [&_h1]:font-semibold [&_h2]:font-heading [&_h2]:text-sm [&_h2]:font-semibold [&_strong]:font-semibold">
      <ReactMarkdown
        components={{
          a: ({ children, href }) => (
            <a
              href={href}
              target="_blank"
              rel="noreferrer"
              className="text-brand underline underline-offset-2"
            >
              {children}
            </a>
          ),
          pre: ({ children }) => (
            <pre className="overflow-x-auto rounded-md border border-border bg-surface-elevated p-3 font-mono text-xs">
              {children}
            </pre>
          ),
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
