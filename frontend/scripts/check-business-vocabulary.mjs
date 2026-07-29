#!/usr/bin/env node
/**
 * Gate de vocabulário do modo Negócio (F1 da campanha).
 *
 * O produto atende pessoas leigas: no modo Negócio nenhuma superfície pode
 * expor vocabulário técnico. Este gate varre os catálogos i18n dos namespaces
 * que aparecem no modo Negócio contra a lista de termos proibidos do léxico
 * oficial e falha (Default-FAIL) em qualquer ocorrência.
 *
 * Default-FAIL de verdade:
 *  - namespace novo, não classificado, é tratado como Negócio (varrido);
 *  - namespace classificado que sumiu do catálogo é violação (classificação
 *    desatualizada mente sobre a cobertura do gate);
 *  - exceção só existe se estiver enumerada em EXEMPTIONS, com motivo.
 *
 * Uso: `node scripts/check-business-vocabulary.mjs` (código 1 em violação).
 */
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const LOCALES_ROOT = path.resolve(HERE, '..', 'src', 'i18n', 'locales');
const LANGUAGES = ['pt-BR', 'en'];

/**
 * Namespaces que só existem nos modos Técnico e Administrador (decisão D7).
 * Ficam fora da varredura porque seu público é técnico — e só por isso.
 */
export const TECHNICAL_NAMESPACES = [
  'agents',
  'architecture',
  'cockpitGovernance',
  'governance',
  'governanceDocs',
  'poAssistant',
  'providers',
  'tools',
];

/**
 * Termo proibido no modo Negócio → o que o léxico manda dizer no lugar.
 * `test` roda sobre o texto do catálogo; a sugestão vai na mensagem de erro.
 */
const FORBIDDEN = {
  'pt-BR': [
    [/\bworktrees?\b/i, 'não existe no modo Negócio: descreva o trabalho, não o mecanismo'],
    [/\bbranch(es)?\b/i, 'não existe no modo Negócio'],
    [/\bcommits?\b/i, 'não existe no modo Negócio'],
    [/\bmerges?\b/i, 'não existe no modo Negócio'],
    [/\bleases?\b/i, 'não existe no modo Negócio'],
    [/\bfencing\b/i, 'não existe no modo Negócio'],
    [/\bheartbeats?\b/i, 'não existe no modo Negócio'],
    [/\btenants?\b/i, 'não existe no modo Negócio'],
    [/\bslugs?\b/i, 'use "Sigla"'],
    [/\bdiagnóstic\w*\b/i, 'não existe no modo Negócio'],
    [/\bprovedor(es)?\b/i, 'use "pessoa da equipe" (provedor nunca aparece)'],
    [/\bproviders?\b/i, 'use "pessoa da equipe"'],
    // "modelo de documento/fluxo/projeto" é gabarito (sentido de negócio, vale);
    // "modelo" sozinho ou de IA/chat é o termo técnico proibido.
    [
      /\bmodelos?\b(?!\s+de\s+(documento|fluxo|projeto|contrato|proposta))/i,
      'use "Modo de trabalho" (gabarito de documento/fluxo continua valendo)',
    ],
    [/\btokens?\b/i, 'não existe no modo Negócio'],
    [/\bcotas?\b/i, 'use "Jornada"'],
    [/\bfleet\b/i, 'use "Quadro da Equipe"'],
    [/\bfrota\b/i, 'use "Quadro da Equipe"'],
    [/\borquestrador(es)?\b/i, 'use "Equipe"'],
    [/\bcards?\b/i, 'use "Tarefa"'],
    [/\btentativas?\b/i, 'use "Rodada de trabalho"'],
    [/\bgates?\b/i, 'use "o que falta para avançar de etapa"'],
    [/\bfases?\b/i, 'use "Etapa"'],
    [/\bagentes?\b/i, 'use o nome da pessoa ou "equipe"'],
    [/\bpersonas?\b/i, 'use "cargo" ou "competência"'],
    [/\bworkflows?\b/i, 'use "fluxo de trabalho"'],
    [/\bendpoints?\b/i, 'não existe no modo Negócio'],
    [/\bpayloads?\b/i, 'não existe no modo Negócio'],
    [/\bwebhooks?\b/i, 'não existe no modo Negócio'],
    [/\bAPI\b/, 'não existe no modo Negócio'],
    [/\bJSON\b/, 'não existe no modo Negócio'],
    [/\bSQL\b/, 'não existe no modo Negócio'],
    [/\bULID\b/, 'não existe no modo Negócio'],
    [/\bUUID\b/, 'não existe no modo Negócio'],
    [/\bCLI\b/, 'não existe no modo Negócio'],
    [/\bHTTPS?\b/, 'não existe no modo Negócio'],
    [/\bmigrations?\b/i, 'não existe no modo Negócio'],
    [/\bschemas?\b/i, 'não existe no modo Negócio'],
    [/\bruntime\b/i, 'não existe no modo Negócio'],
    [/\bsandbox\b/i, 'não existe no modo Negócio'],
    [/\bdeploys?\b/i, 'use "publicar"'],
    [/\brepositórios?\b/i, 'não existe no modo Negócio'],
    [/\bbackend\b/i, 'não existe no modo Negócio'],
    [/\bfrontend\b/i, 'não existe no modo Negócio'],
    [/\bscopeclaims?\b/i, 'não existe no modo Negócio'],
    [/\bthreads?\b/i, 'não existe no modo Negócio'],
    [/\bcheckpoints?\b/i, 'não existe no modo Negócio'],
    [/\bwatchdog\b/i, 'não existe no modo Negócio'],
    [/\boutbox\b/i, 'não existe no modo Negócio'],
    [/\bledger\b/i, 'não existe no modo Negócio'],
  ],
  en: [
    [/\bworktrees?\b/i, 'not in Business mode'],
    [/\bbranch(es)?\b/i, 'not in Business mode'],
    [/\bcommits?\b/i, 'not in Business mode'],
    [/\bmerges?\b/i, 'not in Business mode'],
    [/\bleases?\b/i, 'not in Business mode'],
    [/\bfencing\b/i, 'not in Business mode'],
    [/\bheartbeats?\b/i, 'not in Business mode'],
    [/\btenants?\b/i, 'not in Business mode'],
    [/\bslugs?\b/i, 'use "acronym"'],
    [/\bdiagnostics?\b/i, 'not in Business mode'],
    [/\bproviders?\b/i, 'use "team member"'],
    [/\bmodels?\b/i, 'use "working mode" (or "template", for documents)'],
    [/\btokens?\b/i, 'not in Business mode'],
    [/\bquotas?\b/i, 'use "workload"'],
    [/\bfleet\b/i, 'use "Team board"'],
    [/\borchestrator\b/i, 'use "Team"'],
    [/\bcards?\b/i, 'use "Task"'],
    [/\battempts?\b/i, 'use "work round"'],
    [/\bgates?\b/i, 'use "what is missing to advance"'],
    [/\bphases?\b/i, 'use "stage"'],
    [/\bagents?\b/i, 'use the person name or "team"'],
    [/\bpersonas?\b/i, 'use "role" or "skill"'],
    [/\bendpoints?\b/i, 'not in Business mode'],
    [/\bpayloads?\b/i, 'not in Business mode'],
    [/\bwebhooks?\b/i, 'not in Business mode'],
    [/\bAPI\b/, 'not in Business mode'],
    [/\bJSON\b/, 'not in Business mode'],
    [/\bSQL\b/, 'not in Business mode'],
    [/\bULID\b/, 'not in Business mode'],
    [/\bUUID\b/, 'not in Business mode'],
    [/\bCLI\b/, 'not in Business mode'],
    [/\bHTTPS?\b/, 'not in Business mode'],
    [/\bmigrations?\b/i, 'not in Business mode'],
    [/\bschemas?\b/i, 'not in Business mode'],
    [/\bruntime\b/i, 'not in Business mode'],
    [/\bsandbox\b/i, 'not in Business mode'],
    [/\bdeploys?\b/i, 'use "publish"'],
    [/\brepositor(y|ies)\b/i, 'not in Business mode'],
    [/\bbackend\b/i, 'not in Business mode'],
    [/\bfrontend\b/i, 'not in Business mode'],
    [/\bscopeclaims?\b/i, 'not in Business mode'],
    [/\bthreads?\b/i, 'not in Business mode'],
    [/\bcheckpoints?\b/i, 'not in Business mode'],
    [/\bwatchdog\b/i, 'not in Business mode'],
    [/\boutbox\b/i, 'not in Business mode'],
    [/\bledger\b/i, 'not in Business mode'],
  ],
};

/**
 * Exceções enumeradas: chave (sem idioma) → motivo. Cada linha é dívida
 * declarada, não perdão permanente: a fase dona move a chave para uma
 * subárvore técnica e a linha some daqui.
 */
export const EXEMPTIONS = new Map([
  // Rótulos de itens de menu e telas que só existem nos modos Técnico e
  // Administrador (D7), mas cuja chave ainda mora em namespace de Negócio.
  // A F4 (dona do menu) move para `nav.technical.*`; a linha some daqui.
  ['nav.agents', 'item de menu exclusivo do modo Técnico (D7)'],
  ['nav.providers', 'item de menu exclusivo do modo Técnico (D7)'],
  ['nav.keywords.agents', 'busca do item de menu exclusivo do modo Técnico (D7)'],
  ['features.agents.title', 'tela exclusiva do modo Técnico (D7)'],
  ['features.agents.description', 'tela exclusiva do modo Técnico (D7)'],
  ['features.providers.title', 'tela exclusiva do modo Técnico (D7)'],
  ['features.providers.description', 'tela exclusiva do modo Técnico (D7)'],
  ['features.tools.description', 'tela exclusiva do modo Técnico (D7)'],
  // Rótulos de detalhe técnico exibidos apenas quando o modo Técnico está
  // ligado (a lista completa de serviços é da F8; ferramentas são da F11).
  ['status.mcpTransport.http', 'detalhe exibido só no modo Técnico'],
  ['status.runTargetKind.http', 'detalhe exibido só no modo Técnico'],
  // Subárvores técnicas dentro de namespace de Negócio (`.*` cobre a árvore).
  // D13: o passo a passo de API/curl/token do canal só existe no Técnico (F11-b).
  ['channels.cli.*', 'passo a passo técnico do canal — só no modo Técnico (D13, F11-b)'],
  // D8: no Negócio, Executar Projeto mostra só "Abrir <projeto>"; o roteiro de
  // serviços/logs é do modo Técnico (F8).
  ['runProject.guide.*', 'roteiro técnico de execução — só no modo Técnico (D8, F8)'],
  // Painel de diagnóstico das Configurações: conteúdo técnico (F11-f).
  ['settings.diagnostics.*', 'painel de diagnóstico — só no modo Técnico (F11-f)'],
  // D12: o formulário completo de projeto (repositório, branch, workflow,
  // criticidade) permanece no modo Técnico; a F6 cria o mínimo de Negócio.
  ['projects.form.*', 'formulário completo — só no modo Técnico (D12, F6)'],
  ['projects.config.*', 'configuração avançada — só no modo Técnico (D12, F6)'],
  ['projects.impact.fields.*', 'campos técnicos do aviso de impacto (D12, F6)'],
  ['projects.card.repository', 'card de Negócio não mostra repositório (D12, F6)'],
  // D10: planejamento, diária, fontes e gráficos migram da Central de Entregas
  // para o Assistente de PO (modo Administrador) na F5.
  ['delivery.planning.*', 'migra para o Assistente de PO (Administrador) na F5 (D10)'],
  ['delivery.daily.*', 'migra para o Assistente de PO (Administrador) na F5 (D10)'],
  ['delivery.sources.*', 'migra para o Assistente de PO (Administrador) na F5 (D10)'],
  ['delivery.charts.*', 'migra para o Assistente de PO (Administrador) na F5 (D10)'],
  // D1/F8: a definição de agente (modelo, conta, esforço, risco) é configuração
  // técnica; no Negócio a pessoa aparece com nome, cargo e competências.
  ['orchestrator.definitions.*', 'definição de agente — só no modo Técnico (D1, F8)'],
  ['orchestrator.tabs.definitions', 'aba exclusiva do modo Técnico (D1, F8)'],
  // §2: saúde/heartbeat/lease/fencing/diagnóstico não existem no Negócio.
  ['orchestrator.chief.diagnostics.*', 'diagnóstico — só no modo Técnico (léxico §2, F8)'],
  // Seletor de modelo do compositor: no Negócio existe só "Modo de trabalho".
  ['chat.composer.model', 'seletor de modelo — só no modo Técnico (léxico §2, F2)'],
  ['chat.composer.modelDefault', 'seletor de modelo — só no modo Técnico (léxico §2, F2)'],
  // Busca de itens de menu técnicos/administrativos (D7).
  ['nav.keywords.providers', 'busca de item de menu do modo Técnico (D7)'],
  ['nav.keywords.architecture', 'busca de item de menu do modo Administrador (D7)'],
]);

/** Exceção vale para a chave exata ou para a subárvore declarada com `.*`. */
export function isExempt(key, exemptions) {
  if (exemptions.has(key)) return true;
  for (const exempted of exemptions.keys()) {
    if (exempted.endsWith('.*') && key.startsWith(exempted.slice(0, -1))) return true;
  }
  return false;
}

function readJson(file) {
  return JSON.parse(readFileSync(file, 'utf8'));
}

export function mergedCatalog(lang, root = LOCALES_ROOT) {
  const catalog = readJson(path.join(root, `${lang}.json`));
  for (const file of readdirSync(path.join(root, lang))) {
    if (!file.endsWith('.json')) continue;
    Object.assign(catalog, readJson(path.join(root, lang, file)));
  }
  return catalog;
}

function* leaves(node, prefix) {
  if (typeof node === 'string') {
    yield [prefix, node];
    return;
  }
  if (node && typeof node === 'object') {
    for (const [key, child] of Object.entries(node)) {
      yield* leaves(child, prefix ? `${prefix}.${key}` : key);
    }
  }
}

/**
 * @returns {{key: string, lang: string, term: string, hint: string, text: string}[]}
 */
export function collectVocabularyViolations(
  catalogs,
  { technicalNamespaces = TECHNICAL_NAMESPACES, exemptions = EXEMPTIONS } = {},
) {
  const violations = [];
  const technical = new Set(technicalNamespaces);

  for (const [lang, catalog] of Object.entries(catalogs)) {
    const rules = FORBIDDEN[lang] ?? FORBIDDEN['pt-BR'];
    for (const namespace of Object.keys(catalog)) {
      // Default-FAIL: namespace desconhecido é Negócio até prova em contrário.
      if (technical.has(namespace)) continue;
      for (const [key, text] of leaves(catalog[namespace], namespace)) {
        if (isExempt(key, exemptions)) continue;
        // `{{variavel}}` é nome de interpolação, não texto lido pelo dono.
        const visible = text.replace(/\{\{[^}]*\}\}/g, ' ');
        for (const [pattern, hint] of rules) {
          const match = pattern.exec(visible);
          if (match) violations.push({ key, lang, term: match[0], hint, text });
        }
      }
    }
  }
  return violations;
}

/** Classificação que aponta para namespace inexistente mente sobre cobertura. */
export function staleClassification(catalogs, technicalNamespaces = TECHNICAL_NAMESPACES) {
  const present = new Set(Object.values(catalogs).flatMap((c) => Object.keys(c)));
  return technicalNamespaces.filter((namespace) => !present.has(namespace));
}

export function loadCatalogs(root = LOCALES_ROOT) {
  return Object.fromEntries(LANGUAGES.map((lang) => [lang, mergedCatalog(lang, root)]));
}

function main() {
  const catalogs = loadCatalogs();
  const stale = staleClassification(catalogs);
  const violations = collectVocabularyViolations(catalogs);

  for (const namespace of stale) {
    console.error(`[classificação desatualizada] namespace "${namespace}" não existe mais`);
  }
  for (const violation of violations) {
    console.error(
      `[${violation.lang}] ${violation.key}: "${violation.term}" — ${violation.hint}\n    ${violation.text}`,
    );
  }

  if (stale.length || violations.length) {
    console.error(
      `\nGate de vocabulário do modo Negócio: ${violations.length} violação(ões), ` +
        `${stale.length} classificação(ões) desatualizada(s).`,
    );
    process.exit(1);
  }
  console.log('Gate de vocabulário do modo Negócio: sem violações.');
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
