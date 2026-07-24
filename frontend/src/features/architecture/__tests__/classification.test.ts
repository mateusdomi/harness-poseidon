import { describe, expect, it } from 'vitest';

import type { ArchitectureElement, ArchitectureRelationship } from '../api/types';
import {
  VIEW_PRESETS,
  classifyElement,
  findPreset,
  projectModel,
} from '../model/classification';

function element(
  id: string,
  kind: string,
  properties: Record<string, string> = {},
): ArchitectureElement {
  return {
    id,
    projectId: 'p1',
    kind,
    name: id,
    description: '',
    properties,
    state: 'baseline',
    locked: false,
    version: 1,
  };
}

function relationship(id: string, sourceId: string, targetId: string): ArchitectureRelationship {
  return {
    id,
    projectId: 'p1',
    sourceId,
    targetId,
    kind: 'uses',
    properties: {},
    state: 'baseline',
    version: 1,
  };
}

describe('classifyElement', () => {
  it('mapeia kinds C4 para as camadas corretas', () => {
    expect(classifyElement(element('a', 'person')).c4).toBe('context');
    expect(classifyElement(element('a', 'softwareSystem')).c4).toBe('context');
    expect(classifyElement(element('a', 'container')).c4).toBe('container');
    expect(classifyElement(element('a', 'component')).c4).toBe('component');
    expect(classifyElement(element('a', 'deploymentNode')).c4).toBe('deployment');
  });

  it('mapeia kinds ArchiMate para as camadas corretas', () => {
    expect(classifyElement(element('a', 'businessProcess')).archimate).toBe('business');
    expect(classifyElement(element('a', 'applicationComponent')).archimate).toBe('application');
    expect(classifyElement(element('a', 'dataObject')).archimate).toBe('data');
    expect(classifyElement(element('a', 'systemSoftware')).archimate).toBe('technology');
    expect(classifyElement(element('a', 'goal')).archimate).toBe('motivation');
  });

  it('normaliza kinds com hífen/underscore/caixa', () => {
    expect(classifyElement(element('a', 'Deployment_Node')).c4).toBe('deployment');
    expect(classifyElement(element('a', 'application-component')).archimate).toBe('application');
  });

  it('honra override explícito por propriedade', () => {
    const c = classifyElement(element('a', 'unknownKind', { c4: 'component', archimate: 'data' }));
    expect(c.c4).toBe('component');
    expect(c.archimate).toBe('data');
  });

  it('extrai tags da propriedade tags', () => {
    expect(classifyElement(element('a', 'x', { tags: 'security, network' })).tags).toEqual([
      'security',
      'network',
    ]);
  });
});

describe('VIEW_PRESETS', () => {
  it('cobre C4 (5), ArchiMate (5), extras e modelo completo', () => {
    const c4 = VIEW_PRESETS.filter((p) => p.group === 'c4');
    const archimate = VIEW_PRESETS.filter((p) => p.group === 'archimate');
    expect(c4.map((p) => p.id)).toEqual([
      'c4-context',
      'c4-container',
      'c4-component',
      'c4-deployment',
      'c4-dynamic',
    ]);
    expect(archimate.map((p) => p.id)).toEqual([
      'archimate-business',
      'archimate-application',
      'archimate-data',
      'archimate-technology',
      'archimate-motivation',
    ]);
    expect(VIEW_PRESETS.some((p) => p.id === 'model')).toBe(true);
    for (const extra of ['security', 'cost', 'observability', 'network', 'infrastructure']) {
      expect(VIEW_PRESETS.some((p) => p.id === extra)).toBe(true);
    }
  });

  it('findPreset cai no modelo completo para id inválido', () => {
    expect(findPreset('nope').id).toBe('model');
  });
});

describe('projectModel', () => {
  const elements = [
    element('person', 'person'),
    element('sys', 'softwareSystem'),
    element('api', 'container'),
    element('db', 'database', { tags: 'data' }),
    element('sec', 'trustBoundary', { tags: 'security' }),
    element('goal', 'goal'),
  ];
  const relationships = [
    relationship('r1', 'person', 'sys'),
    relationship('r2', 'sys', 'api'),
    relationship('r3', 'api', 'db'),
    relationship('r4', 'sec', 'api'),
  ];

  it('modelo completo mantém tudo', () => {
    const projected = projectModel(elements, relationships, 'model');
    expect(projected.elements).toHaveLength(6);
    expect(projected.relationships).toHaveLength(4);
  });

  it('C4 contexto mantém apenas pessoas e sistemas', () => {
    const projected = projectModel(elements, relationships, 'c4-context');
    expect(projected.elements.map((e) => e.id).sort()).toEqual(['person', 'sys']);
    // Só relacionamentos com ambos extremos visíveis
    expect(projected.relationships.map((r) => r.id)).toEqual(['r1']);
  });

  it('view de segurança projeta as fronteiras de confiança', () => {
    const projected = projectModel(elements, relationships, 'security');
    expect(projected.elements.map((e) => e.id)).toEqual(['sec']);
    expect(projected.relationships).toHaveLength(0);
  });

  it('view de dados projeta elementos de dados', () => {
    const projected = projectModel(elements, relationships, 'archimate-data');
    expect(projected.elements.map((e) => e.id)).toContain('db');
  });
});
