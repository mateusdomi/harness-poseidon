import { ArrowLeft } from 'lucide-react';
import { useCallback } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';

import { cn } from '@/lib/utils';

export interface BackLinkProps {
  /** Rótulo específico já traduzido, ex.: "Voltar para projetos". */
  label: string;
  /**
   * Handler explícito de voltar. Use em telas cujo "detalhe/criação" é estado
   * local (não uma rota) — o histórico do router não retornaria à lista.
   */
  onBack?: () => void;
  /** Rota-pai usada quando não há histórico de origem válido no app. */
  fallbackTo?: string;
  className?: string;
}

/**
 * Botão "Voltar" compartilhado: seta + rótulo específico, acessível por
 * teclado (é um `button`) e independente do menu lateral.
 *
 * Ordem de resolução: `onBack` explícito → histórico do app (quando a origem
 * é válida) → `fallbackTo` (rota-pai). Preserva o estado da origem porque
 * `navigate(-1)` reusa a entrada anterior do histórico com seus filtros.
 */
export function BackLink({ label, onBack, fallbackTo, className }: BackLinkProps) {
  const navigate = useNavigate();
  const location = useLocation();
  // `key === 'default'` = entrada inicial: não há origem no app para voltar.
  const hasHistoryOrigin = location.key !== 'default';

  const handleClick = useCallback(() => {
    if (onBack) {
      onBack();
      return;
    }
    if (hasHistoryOrigin) {
      navigate(-1);
      return;
    }
    navigate(fallbackTo ?? '/');
  }, [onBack, hasHistoryOrigin, navigate, fallbackTo]);

  return (
    <button
      type="button"
      onClick={handleClick}
      className={cn(
        'inline-flex min-h-touch items-center gap-1.5 self-start rounded-md text-sm font-medium text-foreground-muted',
        'hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand',
        'motion-safe:transition-colors',
        className,
      )}
    >
      <ArrowLeft aria-hidden="true" className="size-4" />
      {label}
    </button>
  );
}
