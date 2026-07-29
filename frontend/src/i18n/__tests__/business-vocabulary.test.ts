import { describe, expect, it } from 'vitest';

// O gate roda como script Node (CI e `npm run check`); o teste consome as
// mesmas funções para que build e CI nunca divirjam do que é varrido.
import {
  EXEMPTIONS,
  TECHNICAL_NAMESPACES,
  collectVocabularyViolations,
  isExempt,
  loadCatalogs,
  staleClassification,
} from '../../../scripts/check-business-vocabulary.mjs';

describe('gate de vocabulário do modo Negócio', () => {
  const catalogs = loadCatalogs();

  it('nenhum termo proibido nos namespaces de Negócio', () => {
    const violations = collectVocabularyViolations(catalogs);
    const readable = violations.map((v) => `[${v.lang}] ${v.key}: "${v.term}" — ${v.text}`);
    expect(readable, `Violações de léxico:\n${readable.join('\n')}`).toEqual([]);
  });

  it('a classificação de namespaces técnicos está atualizada', () => {
    expect(staleClassification(catalogs)).toEqual([]);
  });

  it('varre de fato os namespaces de Negócio (gate não é vácuo)', () => {
    const scanned = Object.keys(catalogs['pt-BR']).filter(
      (namespace) => !TECHNICAL_NAMESPACES.includes(namespace),
    );
    expect(scanned.length).toBeGreaterThan(20);
    expect(scanned).toContain('cockpit');
    expect(scanned).toContain('chat');
    expect(scanned).toContain('board');
  });
});

describe('gate de vocabulário — Default-FAIL', () => {
  it('acusa termo proibido em namespace conhecido', () => {
    const violations = collectVocabularyViolations({
      'pt-BR': { cockpit: { titulo: 'Fase atual do workflow' } },
    });
    expect(violations.map((v) => v.term.toLowerCase())).toEqual(
      expect.arrayContaining(['fase', 'workflow']),
    );
  });

  it('namespace novo, não classificado, é varrido como Negócio', () => {
    const violations = collectVocabularyViolations({
      'pt-BR': { featureQueNasceuAgora: { hint: 'Abra o worktree do agente' } },
    });
    expect(violations.length).toBeGreaterThan(0);
    expect(violations[0].key).toBe('featureQueNasceuAgora.hint');
  });

  it('namespace técnico declarado fica de fora', () => {
    const violations = collectVocabularyViolations({
      'pt-BR': { governance: { hint: 'Abra o worktree do agente' } },
    });
    expect(violations).toEqual([]);
  });

  it('exceção declarada vale para a chave e para a subárvore', () => {
    const exemptions = new Map([
      ['nav.agents', 'motivo'],
      ['settings.diagnostics.*', 'motivo'],
    ]);
    expect(isExempt('nav.agents', exemptions)).toBe(true);
    expect(isExempt('nav.agentsExtra', exemptions)).toBe(false);
    expect(isExempt('settings.diagnostics.title', exemptions)).toBe(true);
    expect(isExempt('settings.diagnostics.keys.api', exemptions)).toBe(true);
    expect(isExempt('settings.workspace.hint', exemptions)).toBe(false);
  });

  it('toda exceção declara um motivo (dívida explícita, não perdão mudo)', () => {
    for (const [key, reason] of EXEMPTIONS) {
      expect(typeof reason, `exceção sem motivo: ${key}`).toBe('string');
      expect(reason.length, `motivo vazio: ${key}`).toBeGreaterThan(10);
    }
  });

  it('classificação que aponta para namespace inexistente é violação', () => {
    expect(
      staleClassification({ 'pt-BR': { cockpit: {} } }, ['governance', 'cockpit']),
    ).toEqual(['governance']);
  });
});
