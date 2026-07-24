import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { sanitizeMermaidSvg } from '@/features/governance-docs/lib/mermaid-sanitize';
import { useThemeStore } from '@/stores/theme-store';

type RenderState =
  | { kind: 'loading' }
  | { kind: 'ready'; svg: string }
  | { kind: 'fallback' };

export function MermaidDiagram({ source }: { source: string }) {
  const { t } = useTranslation();
  const theme = useThemeStore((state) => state.resolved);
  const reactId = useId();
  const [state, setState] = useState<RenderState>({ kind: 'loading' });

  useEffect(() => {
    let active = true;
    const diagramId = `poseidon-mermaid-${reactId.replace(/[^a-zA-Z0-9_-]/g, '')}`;
    setState({ kind: 'loading' });

    void import('mermaid')
      .then(async ({ default: mermaid }) => {
        mermaid.initialize({
          startOnLoad: false,
          securityLevel: 'strict',
          suppressErrorRendering: true,
          theme: theme === 'dark' ? 'dark' : 'neutral',
        });
        const rendered = await mermaid.render(diagramId, source);
        if (active) setState({ kind: 'ready', svg: sanitizeMermaidSvg(rendered.svg) });
      })
      .catch(() => {
        if (active) setState({ kind: 'fallback' });
      });

    return () => {
      active = false;
    };
  }, [reactId, source, theme]);

  if (state.kind === 'ready') {
    return (
      <div
        role="img"
        aria-label={t('governanceDocs.viewer.diagramRendered')}
        className="overflow-x-auto bg-surface p-4 [&_svg]:mx-auto [&_svg]:h-auto [&_svg]:max-w-full"
        // Mermaid gera SVG e o sanitizador local remove superfícies executáveis.
        dangerouslySetInnerHTML={{ __html: state.svg }}
      />
    );
  }

  if (state.kind === 'fallback') {
    return (
      <div className="flex flex-col gap-2 p-3">
        <p className="text-xs text-warning">{t('governanceDocs.viewer.diagramFallback')}</p>
        <pre className="overflow-x-auto font-mono text-xs leading-relaxed">
          {source.replace(/\n$/, '')}
        </pre>
      </div>
    );
  }

  return (
    <p role="status" className="p-3 text-xs text-foreground-muted">
      {t('governanceDocs.viewer.diagramLoading')}
    </p>
  );
}
