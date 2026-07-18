# Evidência F0 PoC-8 — PostgreSQL dual, SKIP LOCKED e fencing

- Executado em: 2026-07-18T13:18:20Z
- Ambiente: macOS arm64, Docker Desktop/Engine 29.5.2, PostgreSQL 18.4, Npgsql/EF provider 10.0.3
- Resultado: verde

## Cenário executado

Cada execução construiu uma imagem `harness-postgres-poc8:<attempt>`, iniciou container, volume e bridge exclusivos com `com.harness.managed=true`/`com.harness.attempt`, publicou `5432` em porta efêmera de `127.0.0.1` e montou uma senha aleatória por arquivo. O container usou 0,5 CPU, 256 MiB e 128 PIDs; autenticação host foi SCRAM-SHA-256.

O teste real validou:

1. A migration PostgreSQL embarcada, serializada por advisory lock, retornou `1` na primeira aplicação e `0` na segunda.
2. O enqueue idempotente criou 80 itens; o replay dos mesmos IDs criou zero.
3. Doze workers concorrentes adquiriram os 80 IDs exatamente uma vez com a CTE `FOR UPDATE SKIP LOCKED`.
4. Uma transação reteve lock em `a-locked`; outro owner adquiriu `b-available` em menos de dois segundos, provando skip em vez de espera.
5. `owner-old` recebeu fencing token 1; após expiração, `owner-new` recebeu token 2. A conclusão com token 1 retornou false e a conclusão com token 2 retornou true.
6. Antes do cleanup havia exatamente um container, uma network, um volume e uma imagem da tentativa. Depois, os quatro inventários retornaram vazios e o segredo/artefato da tentativa foi removido.

## Falhas diagnosticadas e correções

- Docker Desktop 29.5.2 não materializou port binding na bridge `--internal`; uma bridge gerenciada normal com publicação exclusiva em loopback resolveu a incompatibilidade. A política proxy/default-deny de sandboxes não foi relaxada.
- O health da imagem mínima falhou duas vezes. A próxima execução foi instrumentada com estado e logs do container, que mostraram inicialização completa seguida de `FATAL: unrecognized configuration parameter "pgdata"`. O entrypoint passou a usar `postgres -D` e manteve a captura diagnóstica.
- O `pg_hba.conf` inicial aceitava apenas loopback do container. Foi adicionada a regra SCRAM para hosts, enquanto Docker restringe a porta ao loopback do macOS.

## Segurança da imagem

O primeiro scan da imagem oficial `postgres:18.4-alpine3.24` detectou uma crítica: `CVE-2025-68121` no runtime Go 1.24.6 embutido no `gosu`. Remover apenas o arquivo não limpou o SBOM herdado. A imagem final foi reconstruída sobre `alpine:3.24` com PostgreSQL 18.4 e `su-exec`, sem `gosu`.

```text
Target: harness-postgres-poc8:securityscan
Digest: 11ef52b98c8d
Packages indexed: 52
Vulnerabilities: 0C 0H 0M 2L 1?
```

O filtro crítico saiu com code 0 e `No vulnerable packages detected`. Um segundo scan sem filtro confirmou zero crítica, alta e média, além de duas baixas (`CVE-2026-0989`, `CVE-2025-8732`) e uma não classificada (`CVE-2026-11979`) em `libxml2 2.13.9-r2`; nenhuma possui fix disponível no Alpine nesta data. A imagem de scan foi inspecionada (`managed=true`, attempt `poc8-securityscan`) e removida pelo nome exato; nenhum prune foi usado.

## Comandos e saídas

```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~PostgresSkipLockedPocTests
docker scout cves harness-postgres-poc8:securityscan --format packages
tools/backend/verify.sh
```

A versão final do teste PostgreSQL passou seis execuções isoladas consecutivas (3–4 s). O gate completo passou restore locked, format, build Release com 0 warnings/0 errors e 47/47 testes. Contagens finais: 11 containers, 14 volumes, 7 networks e 9 imagens, iguais ao inventário; zero recursos com `com.harness.managed=true`.
