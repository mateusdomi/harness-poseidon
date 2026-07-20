import { featureFlags } from '@/config/features';
import AuditTimelinePage from '@/features/governance/pages/audit-timeline-page';
import GovernanceContractPage from '@/features/governance/pages/governance-contract-page';

/** Rota única de governança; a flag operacional permite rollback imediato para a auditoria. */
export default function GovernancePage() {
  return featureFlags.governanceContractUi ? <GovernanceContractPage /> : <AuditTimelinePage />;
}
