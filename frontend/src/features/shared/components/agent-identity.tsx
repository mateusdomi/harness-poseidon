import { AgentAvatar } from '@/features/shared/components/agent-avatar';
import { resolveAgentIdentity } from '@/lib/agent-persona';
import { cn } from '@/lib/utils';

export interface AgentIdentityProps {
  /** Alias técnico (chave da persona ou alias da conta) para resolver a identidade. */
  alias: string;
  /**
   * Nome da instância a usar quando o alias não está no mapa de personas
   * (ex.: `agent.name`). O nome humano tem prioridade quando existe.
   */
  fallbackName?: string | null;
  /**
   * Subtítulo técnico (transparência). Padrão: o papel amigável resolvido ou,
   * na sua ausência, o próprio alias. Passe explicitamente para exibir o alias
   * de instância (ex.: "Chefe — Poseidon Frontend").
   */
  technicalLabel?: string | null;
  /** Diâmetro do avatar em px (padrão 40). */
  size?: number;
  /** Classe extra da raiz. */
  className?: string;
  /** Classe extra do nome humano (destaque). */
  nameClassName?: string;
}

/**
 * Identidade humanizada de um agente: avatar + nome humano em destaque, com o
 * papel/alias técnico como subtítulo (e no `title`/tooltip) por transparência.
 *
 * Reutilizável em qualquer tela que exiba uma persona ou conta de execução
 * (roster, chat, cabeçalhos). A lógica e os contratos continuam usando o alias
 * técnico original — aqui só muda a APRESENTAÇÃO.
 */
export function AgentIdentity({
  alias,
  fallbackName,
  technicalLabel,
  size = 40,
  className,
  nameClassName,
}: AgentIdentityProps) {
  const identity = resolveAgentIdentity(alias, fallbackName);
  const subtitle = technicalLabel ?? identity.roleLabel ?? identity.alias;

  return (
    <span className={cn('flex min-w-0 items-center gap-2.5', className)}>
      <AgentAvatar name={identity.humanName} size={size} />
      <span className="flex min-w-0 flex-col">
        <span
          className={cn('truncate font-heading font-semibold text-foreground', nameClassName)}
          title={identity.alias || undefined}
        >
          {identity.humanName}
        </span>
        {subtitle ? (
          <span className="truncate text-xs text-foreground-muted">{subtitle}</span>
        ) : null}
      </span>
    </span>
  );
}
