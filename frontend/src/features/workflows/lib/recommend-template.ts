import type { WorkflowTemplate, WorkflowVersion } from '@/api';

export interface TemplateRecommendation {
  template: WorkflowTemplate;
  /** Versão publicada vigente do template (a que será vinculada). */
  version: WorkflowVersion;
}

/**
 * Template recomendado para um projeto novo (§14).
 *
 * LIMITE DE CONTRATO: o backend não publica um campo "recomendado"/default.
 * A recomendação é, portanto, uma heurística explícita e conservadora sobre
 * dados reais — nunca um template inventado:
 * 1. considera apenas templates ativos (não arquivados) com versão publicada
 *    vigente (`currentVersionId` resolvível e versão em estado `published`);
 * 2. entre eles, prefere o mais completo (mais fases) — um caminho guiado
 *    cobre mais do ciclo; empate resolve pelo mais antigo (estável).
 * A ausência de um "template padrão" canônico está registrada em
 * `docs/frontend/HANDOFF_API.md`.
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

  return candidates.sort((a, b) => {
    const byPhases = b.version.phases.length - a.version.phases.length;
    if (byPhases !== 0) return byPhases;
    return a.template.createdAt.localeCompare(b.template.createdAt);
  })[0];
}
