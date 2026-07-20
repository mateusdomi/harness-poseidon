import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

const openApiPath = resolve(process.cwd(), '..', 'docs', 'contracts', 'openapi.json');
const openApi = JSON.parse(readFileSync(openApiPath, 'utf-8')) as {
  paths: Record<string, Record<string, unknown>>;
  components: { schemas: Record<string, { required?: string[] }> };
};

const P1_OPERATIONS = {
  '/api/v1/governance-runtime/receipts': ['get'],
  '/api/v1/governance-runtime/receipts/{turnId}': ['get'],
  '/api/v1/governance-runtime/receipts/{turnId}/metrics': ['get'],
  '/api/v1/governance-runtime/evaluations': ['post'],
  '/api/v1/governance-runtime/stale-doc-findings': ['get'],
  '/api/v1/governance-runtime/projects/{projectId}/hashline-patches': ['post'],
  '/api/v1/governance-runtime/hashline-benchmark': ['get'],
  '/api/v1/governance-runtime/executors': ['get'],
} as const;

describe('drift do OpenAPI de governança P1', () => {
  it('mantém todas as operações consumidas pela UI', () => {
    for (const [path, methods] of Object.entries(P1_OPERATIONS)) {
      expect(openApi.paths[path], `path ausente: ${path}`).toBeDefined();
      for (const method of methods) expect(openApi.paths[path][method]).toBeDefined();
    }
  });

  it.each([
    ['GovernanceReceiptContract', ['turnId', 'manifestVersion', 'documents', 'bundleChecksum', 'state', 'gateResult']],
    ['GovernanceMetricContract', ['eventId', 'kind', 'documentId', 'ruleId', 'tokenCount']],
    ['StaleDocumentFindingContract', ['findingId', 'documentId', 'kind', 'recommendedTask']],
    ['EvaluationResultContract', ['evaluationId', 'verdict', 'findings', 'readOnly', 'cleanContext']],
    ['PatchBenchmarkContract', ['strategy', 'editSuccesses', 'staleRejections', 'regressions']],
    ['AgentExecutorContract', ['id', 'available', 'enabled', 'availabilityReason']],
  ])('%s preserva os campos obrigatórios consumidos', (schemaName, required) => {
    expect(openApi.components.schemas[schemaName]?.required).toEqual(expect.arrayContaining(required));
  });

  it('não trata capacidades P2 ausentes como publicadas', () => {
    const governancePaths = Object.keys(openApi.paths).filter((path) => path.includes('governance'));
    expect(governancePaths.some((path) => /learning|shadow|promotion|rollback|deprecat/i.test(path))).toBe(false);
  });
});
