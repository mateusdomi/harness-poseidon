# Escopo da Fase 0

## Entrada

- Ambiente e Docker inventariados.
- Clone exclusivo validado em `develop`, remote oficial e somente branches permitidas.
- Frontend e documentação frontend preservados e lidos apenas como contrato.

## Entregas

- Solução .NET 10 com estrutura modular congelada e pipeline local único.
- Testes de arquitetura básicos e ADR-001 a ADR-014.
- Nove PoCs executáveis e idempotentes com evidências: SQLite/dispatcher; recuperação abrupta; lease/fencing; Codex CLI; Git fixtures/claims; sandbox; SignalR/re-sync; PostgreSQL; IPC Host–Runner.
- Documentação viva, contratos iniciais e catálogo de evidências.

## Saída GNG-1

As nove PoCs devem terminar verdes em execução real. Falha exige causa raiz, correção, evidência e repetição. Nenhuma existência nominal de projeto ou teste satisfaz o gate.

## Fora do escopo desta fase

- Telas ou mudanças em `frontend/**` e `docs/frontend/**`.
- API completa da Fase 2.
- Merge em `main`, deploy, credenciais reais ou recursos Docker sem label Harness.
