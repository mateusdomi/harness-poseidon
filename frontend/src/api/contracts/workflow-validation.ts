import type { WorkflowPhaseConfig } from './delivery';

/**
 * Validação "do Harness" de uma versão de workflow antes da publicação
 * (FR-4): regras de domínio além do schema zod. Pura e compartilhada —
 * a UI usa para feedback imediato no editor e o mock aplica ao publicar
 * (publicação bloqueada se inválida).
 *
 * Cada issue carrega a CHAVE i18n (`workflows.templates.validation.*`) e
 * parâmetros de interpolação — a tradução acontece na render.
 *
 * Nota sobre "ordem contínua": a ordem das fases é posicional (o array
 * `phases` É a ordem, posições 1..n sempre contíguas), então a regra é
 * estruturalmente garantida pelo modelo — não há checagem separada.
 */

export interface WorkflowValidationIssue {
  /** Chave i18n da mensagem (`workflows.templates.validation.*`). */
  key: string;
  params?: Record<string, string | number>;
}

export interface WorkflowVersionContent {
  phases?: string[] | undefined;
  gatesByPhase?: Record<string, string[]> | undefined;
  phaseConfigs?: Record<string, WorkflowPhaseConfig> | undefined;
  transitions?: Record<string, string[]> | undefined;
}

/** Detecta ciclo simples no grafo de dependências (A→B→A, inclui auto-dependência). */
function findDependencyCycle(dependsOnByPhase: Record<string, string[]>): string[] | null {
  for (const [phase, deps] of Object.entries(dependsOnByPhase)) {
    for (const dep of deps) {
      if (dep === phase) return [phase, phase];
      if ((dependsOnByPhase[dep] ?? []).includes(phase)) return [phase, dep];
    }
  }
  return null;
}

/** Valida o conteúdo de uma versão; retorna as issues (vazio = publicável). */
export function validateWorkflowVersionContent(
  content: WorkflowVersionContent,
): WorkflowValidationIssue[] {
  const issues: WorkflowValidationIssue[] = [];
  const phases = (content.phases ?? []).map((phase) => phase.trim());

  if (phases.length === 0) {
    issues.push({ key: 'workflows.templates.validation.phasesRequired' });
    return issues;
  }
  if (phases.some((phase) => phase === '')) {
    issues.push({ key: 'workflows.templates.validation.emptyPhaseName' });
  }
  const duplicated = phases.filter((phase, index) => phases.indexOf(phase) !== index);
  if (duplicated.length > 0) {
    issues.push({
      key: 'workflows.templates.validation.duplicatePhase',
      params: { phase: [...new Set(duplicated)].join(', ') },
    });
  }

  const known = new Set(phases.filter((phase) => phase !== ''));

  for (const phase of Object.keys(content.gatesByPhase ?? {})) {
    if (!known.has(phase)) {
      issues.push({
        key: 'workflows.templates.validation.unknownGatePhase',
        params: { phase },
      });
    }
  }

  for (const [phase, targets] of Object.entries(content.transitions ?? {})) {
    if (!known.has(phase)) {
      issues.push({
        key: 'workflows.templates.validation.unknownTransitionPhase',
        params: { phase },
      });
    }
    const unknownTarget = targets.find((target) => !known.has(target));
    if (unknownTarget !== undefined) {
      issues.push({
        key: 'workflows.templates.validation.unknownTransitionTarget',
        params: { phase, target: unknownTarget },
      });
    }
  }

  const dependsOnByPhase: Record<string, string[]> = {};
  for (const [phase, config] of Object.entries(content.phaseConfigs ?? {})) {
    if (!known.has(phase)) {
      issues.push({
        key: 'workflows.templates.validation.unknownConfigPhase',
        params: { phase },
      });
      continue;
    }
    dependsOnByPhase[phase] = config.dependsOn ?? [];
    const unknownDependency = (config.dependsOn ?? []).find((dep) => !known.has(dep));
    if (unknownDependency !== undefined) {
      issues.push({
        key: 'workflows.templates.validation.unknownDependency',
        params: { phase, target: unknownDependency },
      });
    }
    const weight = config.progressWeight;
    if (!Number.isFinite(weight) || weight < 0 || weight > 100) {
      issues.push({
        key: 'workflows.templates.validation.invalidWeight',
        params: { phase },
      });
    }
  }

  const cycle = findDependencyCycle(dependsOnByPhase);
  if (cycle) {
    issues.push({
      key: 'workflows.templates.validation.dependencyCycle',
      params: { phase: cycle[0], target: cycle[1] },
    });
  }

  return issues;
}
