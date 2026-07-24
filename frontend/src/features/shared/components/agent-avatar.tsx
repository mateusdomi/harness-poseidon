import { avatarColorsFor, initialsFor } from '@/lib/agent-persona';
import { cn } from '@/lib/utils';

export interface AgentAvatarProps {
  /** Nome humano exibido — fonte das iniciais e (por padrão) da cor. */
  name: string;
  /** Semente opcional da cor (padrão: o próprio nome). */
  seed?: string;
  /** Diâmetro do círculo em px (padrão 40). */
  size?: number;
  className?: string;
}

/**
 * Avatar determinístico de iniciais — círculo colorido com as iniciais do nome.
 *
 * Gerado 100% localmente (iniciais + cor HSL por hash do nome): SEM REDE, sem
 * URL externa, respeitando o CSP. A mesma "pessoa" recebe sempre a mesma cor.
 * É puramente decorativo (`aria-hidden`): o nome acessível vem do texto ao lado
 * (ver {@link AgentIdentity}).
 */
export function AgentAvatar({ name, seed, size = 40, className }: AgentAvatarProps) {
  const colors = avatarColorsFor(seed ?? name);
  return (
    <span
      aria-hidden="true"
      className={cn(
        'inline-flex shrink-0 select-none items-center justify-center rounded-full font-heading font-semibold leading-none',
        className,
      )}
      style={{
        width: size,
        height: size,
        backgroundColor: colors.background,
        color: colors.foreground,
        fontSize: Math.round(size * 0.4),
      }}
    >
      {initialsFor(name)}
    </span>
  );
}
