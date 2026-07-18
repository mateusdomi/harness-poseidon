# ADR-003 — Persistência dual e autoridade exclusiva do Host

- Status: aceito
- Data: 2026-07-18

## Decisão

SQLite atende o modo pessoal e PostgreSQL o modo servidor, com migrations, SQL, índices e testes separados. Somente `Harness.Host` escreve estado de domínio. Runner não abre banco, não migra e envia propostas por IPC autenticado.

## Consequências

Modelo conceitual e invariantes são comuns, mas nenhuma query específica de provider é presumida portável. Concorrência usa `version` crescente gerenciada pela aplicação.
