import { ManagedAgentAvatar } from '@/features/shared/components/managed-agent-avatar';
import { useLeadershipProfile } from '@/features/shared/hooks/use-leadership-profile';
import { isLeadershipAlias } from '@/features/shared/lib/agent-photo';
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
  const leadershipProfile = useLeadershipProfile().data;
  const identity = resolveAgentIdentity(alias, fallbackName);
  const isLeadership = isLeadershipAlias(alias);
  const humanName =
    isLeadership && leadershipProfile ? leadershipProfile.displayName : identity.humanName;
  // O alias técnico NUNCA é texto de apresentação. Ele só aparece quando a tela pede
  // transparência explicitamente, via `technicalLabel` — e telas de negócio não pedem. Enquanto
  // o alias era o último fallback, bastava o backend publicar uma conta fora do mapa de personas
  // (`worker-...`) para o usuário leigo ler o nome da conta no lugar do cargo da pessoa.
  const subtitle = technicalLabel ?? identity.roleLabel ?? null;

  // O tooltip nativo do nome seguia o alias da conta em TODA tela, sem portão de modo: passar o
  // mouse sobre "Bruna Magalhães" no painel revelava `chief-claude-primary`. Vale a mesma regra
  // do subtítulo — o alias só aparece quando a tela pediu transparência técnica; caso contrário
  // o tooltip repete o cargo da pessoa.
  const nameTitle = subtitle ?? undefined;

  return (
    <span className={cn('flex min-w-0 items-center gap-2.5', className)}>
      <ManagedAgentAvatar
        alias={alias}
        fallbackName={fallbackName}
        roleLabel={identity.roleLabel}
        size={size}
      />
      <span className="flex min-w-0 flex-col">
        <span
          className={cn('truncate font-heading font-semibold text-foreground', nameClassName)}
          title={nameTitle}
        >
          {humanName}
        </span>
        {subtitle ? (
          <span className="truncate text-xs text-foreground-muted">{subtitle}</span>
        ) : null}
      </span>
    </span>
  );
}
