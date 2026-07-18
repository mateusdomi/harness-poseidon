# ADR-003 — Persistência dual e autoridade exclusiva do Host

- Status: aceito
- Data: 2026-07-18

## Decisão

SQLite atende o modo pessoal e PostgreSQL o modo servidor, com migrations, SQL, índices e testes separados. Somente `Harness.Host` escreve estado de domínio. Runner não abre banco, não migra e envia propostas por IPC autenticado.

## Consequências

Modelo conceitual e invariantes são comuns, mas nenhuma query específica de provider é presumida portável. Concorrência usa `version` crescente gerenciada pela aplicação.

Na PoC-8, migrations PostgreSQL são recursos SQL embarcados e registrados em `harness_poc.schema_migrations` sob advisory lock transacional. A aquisição concorrente usa uma CTE com `FOR UPDATE SKIP LOCKED`; lease expirada pode ser readquirida com `lease_token` e `version` crescentes, e qualquer conclusão com token anterior é rejeitada pelo predicado transacional. Esse SQL permanece exclusivo do provider PostgreSQL e não é reutilizado pelo SQLite.

O PostgreSQL de integração publica porta dinâmica apenas em `127.0.0.1`, usa senha aleatória por arquivo efêmero, SCRAM-SHA-256, limites de CPU/memória/PIDs e recursos Docker com prefixo/label. A bridge gerenciada não é `--internal`, pois Docker Desktop 29.5.2 descartou o port binding nessa combinação; o banco não inicia conexões externas e a exposição host continua restrita ao loopback. Sandboxes de agentes preservam a topologia proxy/default-deny da ADR-011.
