import { http, HttpResponse } from 'msw';

import { RESOURCE_KINDS, type ResourceKind } from '../contracts';
import { fixtures } from '../fixtures';

/**
 * Handlers msw que servem as fixtures via HTTP (`/api/v1/*`) em desenvolvimento
 * (`VITE_MSW=on`), para inspecionar tráfego real no navegador.
 * Testes NÃO passam por aqui — usam o MockApiClient direto, sem rede.
 */

function isResourceKind(value: string): value is ResourceKind {
  return (RESOURCE_KINDS as string[]).includes(value);
}

function problem(status: number, title: string, detail: string) {
  return HttpResponse.json(
    { type: `https://httpstatuses.com/${status}`, title, status, detail },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );
}

export const handlers = [
  // Listagem com paginação por cursor: GET /api/v1/:resource?cursor=&limit=
  http.get('*/api/v1/:resource', ({ params, request }) => {
    const resource = String(params.resource);
    if (!isResourceKind(resource)) {
      return problem(404, 'Recurso não encontrado', `Recurso "${resource}" não existe.`);
    }
    const url = new URL(request.url);
    const limit = Number(url.searchParams.get('limit') ?? 50);
    const offset = Number(url.searchParams.get('cursor') ?? 0);
    const all = fixtures.data[resource] as unknown[];
    const items = all.slice(offset, offset + limit);
    const nextOffset = offset + limit;
    return HttpResponse.json({
      items,
      nextCursor: nextOffset < all.length ? String(nextOffset) : null,
    });
  }),

  // Detalhe: GET /api/v1/:resource/:id
  http.get('*/api/v1/:resource/:id', ({ params }) => {
    const resource = String(params.resource);
    if (!isResourceKind(resource)) {
      return problem(404, 'Recurso não encontrado', `Recurso "${resource}" não existe.`);
    }
    const item = (fixtures.data[resource] as { id: string }[]).find(
      (entry) => entry.id === params.id,
    );
    if (!item) {
      return problem(404, 'Recurso não encontrado', `${resource}/${String(params.id)} não existe.`);
    }
    return HttpResponse.json(item);
  }),
];
