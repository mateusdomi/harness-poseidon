import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

const contractsRoot = resolve(process.cwd(), '..', 'docs', 'contracts');
const openApi = JSON.parse(readFileSync(resolve(contractsRoot, 'openapi.json'), 'utf-8')) as {
  paths: Record<string, Record<string, Operation>>;
  components: { schemas: Record<string, Schema> };
};
const events = JSON.parse(readFileSync(resolve(contractsRoot, 'events.json'), 'utf-8')) as {
  version: string;
  events: string[];
  envelope: { required: string[] };
};

interface Schema {
  type?: string | string[];
  required?: string[];
  properties?: Record<string, Schema>;
  items?: Schema;
  $ref?: string;
  oneOf?: Schema[];
}

interface Operation {
  parameters?: { name: string; in: string; required?: boolean; schema?: Schema }[];
  requestBody?: { required?: boolean; content: Record<string, { schema: Schema }> };
  responses?: Record<string, { content?: Record<string, { schema: Schema }> }>;
}

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

const P2_OPERATIONS = {
  '/api/v1/governance-runtime/learning-candidates': ['get', 'post'],
  '/api/v1/governance-runtime/learning-candidates/metrics': ['get'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}': ['get'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/evidence': ['get'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/compare': ['get'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/history': ['get'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/review': ['post'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/evaluation-request': ['post'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/evaluations': ['post'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/shadow': ['post'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/decision': ['post'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/promotion': ['post'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/rollback': ['post'],
  '/api/v1/governance-runtime/learning-candidates/{candidateId}/deprecation': ['post'],
} as const;

function schemaRef(operation: Operation, status: string) {
  return operation.responses?.[status]?.content?.['application/json']?.schema.$ref;
}

describe('drift do OpenAPI e eventos de governança P1/P2', () => {
  it('mantém todas as operações consumidas pela UI', () => {
    for (const [path, methods] of Object.entries({ ...P1_OPERATIONS, ...P2_OPERATIONS })) {
      expect(openApi.paths[path], `path ausente: ${path}`).toBeDefined();
      for (const method of methods) expect(openApi.paths[path][method], `${method.toUpperCase()} ${path}`).toBeDefined();
    }
  });

  it('publica paginação e filtros server-side do learning catalog', () => {
    const operation = openApi.paths['/api/v1/governance-runtime/learning-candidates'].get;
    const parameters = operation.parameters ?? [];
    expect(parameters.map((item) => `${item.in}:${item.name}`)).toEqual(expect.arrayContaining([
      'query:organizationId', 'query:projectId', 'query:type', 'query:state', 'query:cursor', 'query:limit',
    ]));
    expect(schemaRef(operation, '200')).toBe('#/components/schemas/LearningCandidatePageContract');
    expect(openApi.components.schemas.LearningCandidatePageContract.required).toEqual(expect.arrayContaining(['items', 'nextCursor', 'total']));
  });

  it.each([
    ['LearningCandidateContract', ['candidateId', 'projectId', 'type', 'state', 'evidence', 'payload', 'evaluationVerdict', 'shadowResult', 'activeVersion', 'previousVersion', 'version']],
    ['LearningCandidatePayload', ['title', 'statement', 'instructions', 'personaId', 'workflowId', 'toolId', 'documentId', 'providerId', 'modelId', 'refinement', 'recommendation', 'correction']],
    ['LearningEvidenceRecord', ['kind', 'reference', 'checksum', 'summary']],
    ['LearningShadowResult', ['sampleSize', 'firstPassSuccessDelta', 'repeatedErrorRateDelta', 'tokenImpact', 'costPerAcceptedTaskDelta', 'regressions', 'evidenceReference']],
    ['LearningCandidateHistoryRecord', ['eventId', 'fromState', 'toState', 'action', 'actorId', 'occurredAt', 'candidateVersion']],
    ['LearningCandidateMetrics', ['created', 'deduplicated', 'rejected', 'approved', 'promoted', 'rolledBack', 'regressionsAfterPromotion']],
  ])('%s preserva os campos obrigatórios consumidos', (schemaName, required) => {
    expect(openApi.components.schemas[schemaName]?.required).toEqual(expect.arrayContaining(required));
  });

  it('mantém schemas de request concretos, enums de histórico e OCC', () => {
    const schemas = openApi.components.schemas;
    for (const requestName of ['LearningTransitionRequest', 'LearningEvaluationRequest', 'LearningShadowRequest', 'LearningDecisionRequest']) {
      expect(schemas[requestName].required).toContain('expectedVersion');
    }
    expect(schemas.LearningCandidateState.type).toBe('integer');
    expect(schemas.LearningCandidateAction.type).toBe('integer');
    expect(schemas.LearningCandidateHistoryRecord.properties?.fromState.$ref).toBe('#/components/schemas/LearningCandidateState');
    expect(schemas.LearningCandidateHistoryRecord.properties?.action.oneOf).toEqual(expect.arrayContaining([
      expect.objectContaining({ $ref: '#/components/schemas/LearningCandidateAction' }),
    ]));
  });

  it('mantém sessão/autorização publicadas e path params obrigatórios', () => {
    const collection = openApi.paths['/api/v1/governance-runtime/learning-candidates'];
    expect(collection.get.responses).toHaveProperty('401');
    expect(collection.post.responses).toHaveProperty('401');
    expect(collection.post.responses).toHaveProperty('409');
    for (const [path, methods] of Object.entries(P2_OPERATIONS)) {
      if (!path.includes('{candidateId}')) continue;
      for (const method of methods) {
        expect(openApi.paths[path][method].parameters).toEqual(expect.arrayContaining([
          expect.objectContaining({ name: 'candidateId', in: 'path', required: true }),
        ]));
      }
    }
  });

  it('usa somente o evento canônico 1.1 publicado para atualização P2', () => {
    expect(events.version).toBe('1.1');
    expect(events.envelope.required).toEqual(expect.arrayContaining(['stream', 'sequence', 'type', 'occurredAt', 'payload']));
    expect(events.events).toContain('audit.eventAppended');
    expect(events.events.some((event) => /learning|candidate|shadow|promot|rollback|deprecat/i.test(event))).toBe(false);
  });
});
