import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ImageOff } from 'lucide-react';

export interface ReferenceImageProps {
  src: string;
  alt: string;
  className?: string;
}

/**
 * Imagem de referência visual com fallback: se o asset falhar (404, rede),
 * mostra um estado visual i18n de "imagem indisponível" em vez do ícone
 * de imagem quebrada do navegador.
 */
export function ReferenceImage({ src, alt, className }: ReferenceImageProps) {
  const { t } = useTranslation();
  const [failed, setFailed] = useState(false);

  if (failed) {
    return (
      <div
        role="img"
        aria-label={alt}
        className="flex h-24 flex-col items-center justify-center gap-1 rounded border border-border bg-surface-elevated text-foreground-muted"
      >
        <ImageOff aria-hidden="true" className="size-6" />
        <span className="text-xs">{t('prototypes.imageUnavailable')}</span>
      </div>
    );
  }

  return <img src={src} alt={alt} className={className} onError={() => setFailed(true)} />;
}
