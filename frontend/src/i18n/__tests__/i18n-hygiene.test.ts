import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

import { describe, expect, it } from 'vitest';
import ts from 'typescript';

/**
 * Higiene de i18n (FE-4):
 *
 * 1. Paridade pt-BR ↔ en — as árvores de chaves dos catálogos mesclados
 *    (base + módulos por feature) precisam ser idênticas.
 *
 * 2. Zero string hardcoded — varredura estática (AST via TypeScript) dos
 *    `src/**\/*.tsx`: falha se encontrar literal de texto visível em JSX
 *    (texto entre tags, `{'...'}` ou atributos aria-label/placeholder/title/alt)
 *    contendo letras. Símbolos, números e pontuação passam; exceções reais
 *    entram na ALLOWLIST abaixo com justificativa.
 *
 * Excluídos da varredura: testes (`__tests__`, *.test.*), stories e `src/test/`.
 */

const SRC_ROOT = path.resolve(__dirname, '..', '..');
const LOCALES_ROOT = path.join(SRC_ROOT, 'i18n', 'locales');
const LANGUAGES = ['pt-BR', 'en'] as const;

const VISIBLE_ATTRIBUTES = new Set(['aria-label', 'placeholder', 'title', 'alt']);

/**
 * Exceções explícitas ao sweep (texto com letras permitido fora do i18n).
 * Formato: `<caminho relativo>:<texto>`. Manter vazio — prefira catálogo.
 */
const ALLOWLIST = new Set<string>([]);

function hasLetter(text: string): boolean {
  return /\p{L}/u.test(text);
}

function collectTsxFiles(dir: string): string[] {
  const entries = readdirSync(dir, { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === '__tests__' || entry.name === 'node_modules') continue;
      files.push(...collectTsxFiles(full));
      continue;
    }
    if (!entry.name.endsWith('.tsx')) continue;
    if (/\.(test|spec|stories)\.tsx$/.test(entry.name)) continue;
    if (full.includes(`${path.sep}src${path.sep}test${path.sep}`)) continue;
    files.push(full);
  }
  return files;
}

function readJson(file: string): Record<string, unknown> {
  return JSON.parse(readFileSync(file, 'utf8')) as Record<string, unknown>;
}

/** Replica o merge de `src/i18n/index.ts`: base + módulos por feature. */
function mergedCatalog(lang: string): Record<string, unknown> {
  const catalog = readJson(path.join(LOCALES_ROOT, `${lang}.json`));
  const modulesDir = path.join(LOCALES_ROOT, lang);
  for (const file of readdirSync(modulesDir)) {
    if (!file.endsWith('.json')) continue;
    Object.assign(catalog, readJson(path.join(modulesDir, file)));
  }
  return catalog;
}

function flattenKeys(value: unknown, prefix = ''): string[] {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    return [prefix];
  }
  return Object.entries(value as Record<string, unknown>).flatMap(([key, child]) =>
    flattenKeys(child, prefix ? `${prefix}.${key}` : key),
  );
}

describe('i18n — paridade pt-BR ↔ en', () => {
  it('árvores de chaves idênticas nos dois idiomas', () => {
    const catalogs = Object.fromEntries(
      LANGUAGES.map((lang) => [lang, new Set(flattenKeys(mergedCatalog(lang)))]),
    ) as Record<(typeof LANGUAGES)[number], Set<string>>;

    const missingInEn = [...catalogs['pt-BR']].filter((key) => !catalogs.en.has(key));
    const missingInPtBr = [...catalogs.en].filter((key) => !catalogs['pt-BR'].has(key));

    expect(
      missingInEn,
      `Chaves presentes em pt-BR e ausentes em en:\n${missingInEn.join('\n')}`,
    ).toEqual([]);
    expect(
      missingInPtBr,
      `Chaves presentes em en e ausentes em pt-BR:\n${missingInPtBr.join('\n')}`,
    ).toEqual([]);
  });
});

describe('i18n — zero string hardcoded em JSX', () => {
  it('nenhum literal de texto visível fora dos catálogos', () => {
    const violations: string[] = [];

    for (const file of collectTsxFiles(SRC_ROOT)) {
      const relative = path.relative(SRC_ROOT, file);
      const source = ts.createSourceFile(
        file,
        readFileSync(file, 'utf8'),
        ts.ScriptTarget.Latest,
        true,
        ts.ScriptKind.TSX,
      );

      const report = (node: ts.Node, text: string, kind: string) => {
        const trimmed = text.trim();
        if (!hasLetter(trimmed)) return;
        if (ALLOWLIST.has(`${relative}:${trimmed}`)) return;
        const { line } = source.getLineAndCharacterOfPosition(node.getStart());
        violations.push(`${relative}:${line + 1} [${kind}] "${trimmed.slice(0, 60)}"`);
      };

      const visit = (node: ts.Node): void => {
        if (ts.isJsxText(node)) {
          report(node, node.getText(source), 'JSXText');
        } else if (ts.isJsxAttribute(node)) {
          const name = node.name.getText(source);
          if (
            VISIBLE_ATTRIBUTES.has(name) &&
            node.initializer &&
            ts.isStringLiteral(node.initializer)
          ) {
            report(node.initializer, node.initializer.text, `@${name}`);
          }
        } else if (ts.isJsxExpression(node)) {
          // {'texto'} direto dentro de JSX
          if (node.expression && ts.isStringLiteral(node.expression)) {
            report(node.expression, node.expression.text, "{'...'}");
          }
        }
        ts.forEachChild(node, visit);
      };
      visit(source);
    }

    expect(violations, `Strings visíveis hardcoded:\n${violations.join('\n')}`).toEqual([]);
  });
});
