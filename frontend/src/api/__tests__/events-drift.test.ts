import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

import { describe, expect, it } from 'vitest';

import { EVENT_PAYLOAD_SCHEMAS, EVENT_TYPES } from '../contracts';

/**
 * Teste de drift contra o catálogo canônico de eventos do backend
 * (`docs/contracts/events.json`): falha se o backend publicar um evento
 * que o frontend ainda não conhece (ou se um evento sumir do catálogo
 * enquanto o frontend ainda o referencia).
 */
// cwd do vitest é a raiz do frontend → o monorepo fica um nível acima.
const catalogPath = resolve(process.cwd(), '..', 'docs', 'contracts', 'events.json');
const catalog = JSON.parse(readFileSync(catalogPath, 'utf-8')) as {
  version: string;
  hub: string;
  clientMethod: string;
  snapshotEndpoint: string;
  envelope: { required: string[] };
  events: string[];
};

describe('drift do catálogo canônico de eventos (docs/contracts/events.json)', () => {
  it('todo evento canônico tem schema de payload no frontend', () => {
    for (const type of catalog.events) {
      expect(
        EVENT_PAYLOAD_SCHEMAS[type as keyof typeof EVENT_PAYLOAD_SCHEMAS],
        `evento canônico "${type}" sem schema no frontend`,
      ).toBeDefined();
    }
  });

  it('o frontend não referencia eventos fora do catálogo canônico', () => {
    const canonical = new Set(catalog.events);
    for (const type of EVENT_TYPES) {
      expect(canonical.has(type), `evento "${type}" não consta no catálogo canônico`).toBe(true);
    }
  });

  it('hub, método e envelope batem com as convenções do frontend', () => {
    expect(catalog.hub).toBe('/hubs/events');
    expect(catalog.clientMethod).toBe('event');
    expect(catalog.envelope.required).toEqual(
      expect.arrayContaining(['stream', 'sequence', 'type', 'occurredAt', 'payload']),
    );
    expect(catalog.snapshotEndpoint).toContain('/api/v1/event-streams/snapshot');
  });
});
