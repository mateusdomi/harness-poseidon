# Auto-key versionado de `agent_key` (P1, baixo acoplamento)

Data: 2026-07-22. Branch: `develop`. Base: `cf3a82e`.

## Objetivo

Fatia P1 de baixo acoplamento (a tarefa backend sugerida pela missão): derivar a chave
(slug) de uma definição de agente de forma DETERMINÍSTICA e versionada quando o cliente não
a informa, sem quebrar contrato nem exigir as contas bloqueadas por OAuth.

## Gerador puro — `AgentKeyGenerator`

`Harness.Persistence.Abstractions.Agents.AgentKeyGenerator` (sem IO, determinístico):

- `Derive(name)`: remove acentos (`ç`→`c`, `ã`→`a`), baixa a caixa, troca qualquer caractere
  fora de `[a-z0-9]` por hífen, colapsa hifens repetidos, apara as bordas e limita a 100
  caracteres. Nome sem caractere aproveitável (vazio, só símbolos, só CJK) vira o fallback
  estável `agent`. O alfabeto é EXATAMENTE o validado pelo store, então uma chave gerada
  nunca é recusada pela validação.
- `Generate(name, existingKeys)`: usa o slug base; se ele já existe (case-insensitive),
  acrescenta o menor sufixo `-N` (N ≥ 2) livre — a "versão" da chave —, truncando a base
  para caber em 100 caracteres quando necessário.

## Fiação no endpoint (backward-compatible)

`POST /api/v1/agent-definitions` (`AgentEndpoints.CreateDefinitionAsync`): quando `Key` vem
vazio/branco, o endpoint lista as chaves do tenant (`ListDefinitionsForTenantAsync`,
incluindo arquivadas) e deriva a chave versionada; uma `Key` informada explicitamente é
preservada sem alteração. **Nenhuma mudança de shape de contrato**: `Key` continua `string`
no `AgentDefinitionWriteRequest` — antes um `Key` vazio dava erro de validação; agora é
auto-derivado. O `UNIQUE` do banco continua o árbitro final: uma corrida cai no `409` já
tratado.

## Testes

- **`AgentKeyGeneratorTests` (17, unit)**: slug de nomes reais e acentuados, fallback para
  nome sem caractere válido, cap de 100 sem hífen final, chave base livre, sufixo versionado
  crescente (`reviewer` → `reviewer-4`), colisão case-insensitive (`REVIEWER` → `reviewer-2`),
  sufixo respeitando o comprimento máximo, e determinismo.
- **`CatalogStoreBehavior` (integração, SQLite sem Docker)**: a MESMA composição do endpoint
  (listar chaves do tenant → `Generate` → criar) contra o store real: a base
  `provider-neutral-reviewer` existe, a chave gerada é `provider-neutral-reviewer-2`, e o
  store a aceita (versão 1, chave distinta).

Regressão verde: UnitTests 294/294, ContractTests 41/41 (sem drift — `Key` permanece
`string`), build Release 0 warnings, format limpo.

## Limite honesto

O cap de 500 definições listadas para deduplicação cobre o catálogo pessoal com folga; acima
disso, o `UNIQUE` do banco continua garantindo unicidade (o gerador só reduz colisões, não
substitui a restrição). A fatia foi entregue diretamente e de forma governada porque o actor
`worker-claude-secondary` do Piloto 2 está bloqueado por OAuth (ver
`PILOTO-2-BLOCKED-EXTERNAL-OAUTH.md`).
