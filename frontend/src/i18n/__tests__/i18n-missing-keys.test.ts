import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

import { describe, expect, it } from 'vitest';
import ts from 'typescript';
import { z } from 'zod';

import * as contractEnums from '@/api/contracts/enums';

/**
 * Gate de chave ausente (F1 · D19): chave que não existe no catálogo vira
 * texto cru na tela do dono (`status.chiefTurnState.delegating`). Aqui a
 * ausência quebra o build, em três varreduras:
 *
 * 1. Chave literal — `t('a.b.c')` precisa existir nos dois idiomas.
 * 2. Cobertura de contrato — todo valor de enum do backend que vira rótulo
 *    (`status.<enum>.<valor>`) precisa ter tradução. Esta é a varredura que
 *    pega o D19: o enum ganhou estados novos e o catálogo ficou para trás.
 * 3. Família dinâmica — `t(`prefixo.${x}`)` exige que o prefixo exista.
 *
 * As funções de checagem são puras e testadas contra catálogo sintético, para
 * provar que o gate falha de verdade (Default-FAIL) e não passa por vacuidade.
 */

const SRC_ROOT = path.resolve(__dirname, '..', '..');
const LOCALES_ROOT = path.join(SRC_ROOT, 'i18n', 'locales');
const LANGUAGES = ['pt-BR', 'en'] as const;

type Catalog = Record<string, unknown>;

function readJson(file: string): Catalog {
  return JSON.parse(readFileSync(file, 'utf8')) as Catalog;
}

/** Replica o merge de `src/i18n/index.ts`: base + módulos por feature. */
function mergedCatalog(lang: string): Catalog {
  const catalog = readJson(path.join(LOCALES_ROOT, `${lang}.json`));
  for (const file of readdirSync(path.join(LOCALES_ROOT, lang))) {
    if (!file.endsWith('.json')) continue;
    Object.assign(catalog, readJson(path.join(LOCALES_ROOT, lang, file)));
  }
  return catalog;
}

function lookup(catalog: Catalog, key: string): unknown {
  return key
    .split('.')
    .reduce<unknown>(
      (node, segment) =>
        node && typeof node === 'object'
          ? (node as Record<string, unknown>)[segment]
          : undefined,
      catalog,
    );
}

/** Sufixos de plural do i18next: `count_one`, `count_other`… */
const PLURAL_SUFFIXES = ['_zero', '_one', '_two', '_few', '_many', '_other'] as const;

export function hasLeaf(catalog: Catalog, key: string): boolean {
  if (typeof lookup(catalog, key) === 'string') return true;
  // Chave com plural existe quando qualquer forma plural está no catálogo.
  return PLURAL_SUFFIXES.some(
    (suffix) => typeof lookup(catalog, `${key}${suffix}`) === 'string',
  );
}

export function hasSubtree(catalog: Catalog, key: string): boolean {
  const node = lookup(catalog, key);
  return node !== null && typeof node === 'object';
}

/** Valores de enum sem rótulo em `status.<nome>` — o defeito D19 generalizado. */
export function missingEnumLabels(
  catalog: Catalog,
  enums: Record<string, readonly string[]>,
): string[] {
  const missing: string[] = [];
  for (const [name, options] of Object.entries(enums)) {
    if (!hasSubtree(catalog, `status.${name}`)) continue;
    for (const option of options) {
      if (!hasLeaf(catalog, `status.${name}.${option}`)) {
        missing.push(`status.${name}.${option}`);
      }
    }
  }
  return missing;
}

/** Enums de contrato (`<nome>Schema`) reduzidos a `<nome> -> opções`. */
function contractEnumOptions(): Record<string, readonly string[]> {
  const options: Record<string, readonly string[]> = {};
  for (const [exportName, value] of Object.entries(contractEnums)) {
    if (!exportName.endsWith('Schema')) continue;
    if (!(value instanceof z.ZodEnum)) continue;
    options[exportName.slice(0, -'Schema'.length)] = value.options as string[];
  }
  return options;
}

interface KeyUsage {
  file: string;
  line: number;
  key: string;
  dynamic: boolean;
}

function collectSourceFiles(dir: string): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === '__tests__' || entry.name === 'node_modules') continue;
      files.push(...collectSourceFiles(full));
      continue;
    }
    if (!/\.tsx?$/.test(entry.name)) continue;
    if (/\.(test|spec|stories)\.tsx?$/.test(entry.name)) continue;
    if (full.includes(`${path.sep}src${path.sep}test${path.sep}`)) continue;
    files.push(full);
  }
  return files;
}

/** Chamada de tradução: `t(...)`, `i18n.t(...)`, `translate(...)`. */
function isTranslateCallee(node: ts.Expression, source: ts.SourceFile): boolean {
  const text = ts.isPropertyAccessExpression(node)
    ? node.name.getText(source)
    : node.getText(source);
  return text === 't';
}

/** `defaultValue` explícito é fallback deliberado — não é chave ausente. */
function hasDefaultValue(call: ts.CallExpression, source: ts.SourceFile): boolean {
  return call.arguments.slice(1).some((argument) => {
    if (ts.isStringLiteral(argument)) return true;
    if (!ts.isObjectLiteralExpression(argument)) return false;
    return argument.properties.some(
      (property) => property.name?.getText(source) === 'defaultValue',
    );
  });
}

export function collectKeyUsages(files: readonly string[]): KeyUsage[] {
  const usages: KeyUsage[] = [];
  for (const file of files) {
    const source = ts.createSourceFile(
      file,
      readFileSync(file, 'utf8'),
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TSX,
    );
    const visit = (node: ts.Node): void => {
      if (ts.isCallExpression(node) && isTranslateCallee(node.expression, source)) {
        const [first] = node.arguments;
        const { line } = source.getLineAndCharacterOfPosition(node.getStart());
        const relative = path.relative(SRC_ROOT, file);
        if (first && ts.isStringLiteral(first) && !hasDefaultValue(node, source)) {
          usages.push({ file: relative, line: line + 1, key: first.text, dynamic: false });
        } else if (first && ts.isTemplateExpression(first)) {
          // `prefixo.${x}` / `prefixo.${x}.sufixo` — vale o prefixo estático.
          const prefix = first.head.text.replace(/\.$/, '');
          if (prefix && !hasDefaultValue(node, source)) {
            usages.push({ file: relative, line: line + 1, key: prefix, dynamic: true });
          }
        }
      }
      ts.forEachChild(node, visit);
    };
    visit(source);
  }
  return usages;
}

const catalogs = Object.fromEntries(
  LANGUAGES.map((lang) => [lang, mergedCatalog(lang)]),
) as Record<(typeof LANGUAGES)[number], Catalog>;

describe('i18n — chave ausente quebra o build', () => {
  it('toda chave literal usada no código existe nos dois idiomas', () => {
    const usages = collectKeyUsages(collectSourceFiles(SRC_ROOT)).filter(
      (usage) => !usage.dynamic,
    );
    expect(usages.length).toBeGreaterThan(50); // a varredura precisa estar viva

    const violations = usages.flatMap((usage) =>
      LANGUAGES.filter((lang) => !hasLeaf(catalogs[lang], usage.key)).map(
        (lang) => `${usage.file}:${usage.line} [${lang}] ${usage.key}`,
      ),
    );

    expect(violations, `Chaves inexistentes:\n${violations.join('\n')}`).toEqual([]);
  });

  it('toda família de chave dinâmica tem subárvore no catálogo', () => {
    const usages = collectKeyUsages(collectSourceFiles(SRC_ROOT)).filter(
      (usage) => usage.dynamic,
    );
    expect(usages.length).toBeGreaterThan(20);

    const violations = usages.flatMap((usage) =>
      LANGUAGES.filter(
        (lang) =>
          !hasSubtree(catalogs[lang], usage.key) && !hasLeaf(catalogs[lang], usage.key),
      ).map((lang) => `${usage.file}:${usage.line} [${lang}] ${usage.key}.*`),
    );

    expect(violations, `Famílias sem catálogo:\n${violations.join('\n')}`).toEqual([]);
  });

  it('todo valor de enum do contrato tem rótulo traduzido (D19)', () => {
    const enums = contractEnumOptions();
    expect(Object.keys(enums).length).toBeGreaterThan(20);

    const violations = LANGUAGES.flatMap((lang) =>
      missingEnumLabels(catalogs[lang], enums).map((key) => `[${lang}] ${key}`),
    );

    expect(
      violations,
      `Estados do contrato sem rótulo (vazariam a chave crua na tela):\n${violations.join('\n')}`,
    ).toEqual([]);
  });
});

describe('i18n — o gate falha de verdade (Default-FAIL)', () => {
  it('acusa valor de enum sem rótulo', () => {
    const catalog = { status: { chiefTurnState: { pending: 'Na fila' } } };
    expect(
      missingEnumLabels(catalog, { chiefTurnState: ['pending', 'delegating'] }),
    ).toEqual(['status.chiefTurnState.delegating']);
  });

  it('não confunde subárvore com folha', () => {
    const catalog = { status: { taskState: { done: 'Pronto' } } };
    expect(hasLeaf(catalog, 'status.taskState')).toBe(false);
    expect(hasSubtree(catalog, 'status.taskState')).toBe(true);
    expect(hasLeaf(catalog, 'status.taskState.done')).toBe(true);
    expect(hasLeaf(catalog, 'status.taskState.missing')).toBe(false);
  });

  it('ignora enum que não vira rótulo de status', () => {
    expect(missingEnumLabels({}, { theme: ['dark', 'light'] })).toEqual([]);
  });
});
