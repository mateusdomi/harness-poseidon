# ADR-015 — Arquitetura documental de governança de agentes

- Status: aceito
- Data: 2026-07-20

## Contexto

A auditoria de governança encontrou prompts normativos fora do Git, nenhum
entrypoint carregável por Claude/Codex, ausência de manifest e índice, dois
threat models divergentes e regras de coordenação sem enforcement autoritativo.
O produto já possui persistência dual, claims, sandbox, auditoria e gates; o
delta está na seleção, proveniência e verificação mecânica do contexto.

## Decisão

Adotamos uma fonte canônica versionada em `governance/`: núcleo curto, regras
temáticas, manifest YAML validado por JSON Schema, prompts históricos e
templates. `CLAUDE.md`, `AGENTS.md` e `docs/INDEX.md` são projeções geradas e o
CI rejeita edição manual ou drift. Não haverá `KIMI.md` sem evidência de que o
provider o carrega.

O enforcement autoritativo é a composição de `tools/backend/verify.sh`, CI e
policy do runtime. Hooks locais são conveniência instalável. Documentos
históricos e superseded nunca entram em carga automática; relatórios de pesquisa
são históricos e on-demand.

O threat model canônico é
[`docs/backend/security/THREAT_MODEL.md`](../backend/security/THREAT_MODEL.md),
por ser o mais recente, abranger todas as superfícies de produção e registrar
mitigações verificadas. Ele substitui
[`docs/security/THREAT_MODEL.md`](../security/THREAT_MODEL.md), mantido apenas
como redirect histórico curto.

`CURRENT_STATE.md` continua manual até existir uma projeção real. Marcadores
explícitos separam a seção factual auditada da seção manual de bloqueios,
suposições e próximos passos; nenhuma seção é rotulada como gerada antes disso.

## Consequências

Toda documentação Markdown ativa deve estar no manifest ou numa allowlist
explícita e justificada. Conflitos, links quebrados, paths locais, fontes fora do
Git e adapters divergentes falham no gate. Mudanças no canônico exigem
regeneração das projeções. A migração é incremental e preserva histórico,
proveniência e checksums sem transformar prompts antigos em contexto ativo.
