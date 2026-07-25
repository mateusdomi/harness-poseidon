import { ManagedAgentAvatar } from '@/features/shared/components/managed-agent-avatar';

export function BrunaProfileAvatar({
  size = 40,
  className,
}: {
  size?: number;
  className?: string;
}) {
  return <ManagedAgentAvatar alias="chief-orchestrator" size={size} className={className} />;
}
