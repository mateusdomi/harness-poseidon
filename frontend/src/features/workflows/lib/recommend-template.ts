import type { WorkflowTemplate, WorkflowVersion } from '@/api';

export interface TemplateRecommendation {
  template: WorkflowTemplate;
  /** Versão publicada vigente do template (a que será vinculada). */
  version: WorkflowVersion;
}

/**
 * Template recomendado para um projeto novo (§14).
 *
 * O backend publica o RECOMENDADO canônico (`recommended: true` — a esteira do
 * playbook): quando presente e publicável, ele vence sem heurística. O fallback
 * (instalações sem flag) permanece a heurística conservadora sobre dados reais:
 * 1. considera apenas templates ativos (não arquivados) com versão publicada
 *    vigente (`currentVersionId` resolvível e versão em estado `published`);
 * 2. entre eles, prefere o mais completo (mais fases); empate resolve pelo
 *    mais antigo (estável).
 */
export function recommendWorkflowTemplate(
  templates: readonly WorkflowTemplate[],
  versions: readonly WorkflowVersion[],
): TemplateRecommendation | null {
  const candidates: TemplateRecommendation[] = [];

  for (const template of templates) {
    if (template.state === 'archived' || template.currentVersionId === null) continue;
    const version = versions.find(
      (candidate) =>
        candidate.id === template.currentVersionId && candidate.state === 'published',
    );
    if (!version) continue;
    candidates.push({ template, version });
  }

  if (candidates.length === 0) return null;

  const canonical = candidates.find((candidate) => candidate.template.recommended);
  if (canonical) return canonical;

  return candidates.sort((a, b) => {
    const byPhases = b.version.phases.length - a.version.phases.length;
    if (byPhases !== 0) return byPhases;
    return a.template.createdAt.localeCompare(b.template.createdAt);
  })[0];
}
