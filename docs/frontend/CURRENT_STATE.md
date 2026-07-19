# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase:** refinamento funcional FR-1 a FR-5 concluído no frontend em 2026-07-19; a evidência detalhada está em `REFINEMENT_AUDIT.md`.
- **Branch:** somente `develop`. Nunca fazer merge em `main` sem autorização explícita.
- **Escopo de autoria:** somente `frontend/**` e `docs/frontend/**`. Há trabalho de backend em paralelo; antes de publicar, buscar `origin/develop`, incorporar apenas o avanço remoto e adicionar ao commit somente esses dois diretórios.
- **Design:** o design system, tokens, temas, logo, cores, tipografia e padrões responsivos existentes foram preservados. O trabalho foi incremental.
- **Integração:** contas/providers/modelos e o lifecycle V3 de `agent-definitions` estão reconciliados com `docs/contracts/openapi.json`; o catálogo de eventos e o snapshot realtime têm validação de contrato. Alguns metadados complementares do refinamento (time, stacks, effort/account/fallback padrão, actor/critic, risco e histórico legível) ainda são mock-only e estão registrados em `HANDOFF_API.md`.

## Refinamentos entregues

- FR-1: paginação 15/30/50, command palette, ordem do menu, assets locais e flags do React Router.
- FR-2: período 24h/3d/7d no Cockpit e painel de workflow no Chat, responsivo e com deep-links.
- FR-3: revisão manual/versionada de documentos; busca, filtros, arquivamento, CSV e explicação de fluxo no Quadro.
- FR-4: ciclo completo de templates/versões de workflow e impacto/versionamento de projetos.
- FR-5: CRUD de definições no Orquestrador, organograma operacional em Agentes, explicação do modo local em Executar projeto e gestão de múltiplas contas/metadados/effort mapping em Provedores.

## Regras permanentes

- Não recriar o frontend nem alterar o backend durante refinamentos de UI.
- Não inventar silenciosamente contrato ausente: Zod + mock + teste + `HANDOFF_API.md`.
- Segredos são recebidos apenas como referência segura (`keychain://`, `dpapi://` ou `secret://`) e nunca retornados/exibidos.
- Estados de coleção: vazio orientado, skeleton, erro com retry e reconexão global; 401/403 ainda dependem do contrato de autorização.
- Zero string visível fora do i18n, zero cor fora dos tokens e zero status sem enum/mapeamento.

## Como validar

```bash
cd frontend
npm ci
npm run check
npm run build
npm run build-storybook
npm run test:e2e
npm run test:a11y
```

O modo de integração usa `VITE_API_MODE=http` e `VITE_API_BASE_URL`; o modo padrão continua sendo o mock determinístico.
