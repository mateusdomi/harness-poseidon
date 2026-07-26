# Documentação

## Catálogo fechado

`governance/manifest.yaml` é o catálogo único dos documentos. Todo Markdown deve
ter uma entrada no manifest no mesmo commit em que nasce, com identidade, path,
autoridade, owner, audiência, escopos, política `load`, custo estimado de tokens,
checksum e fonte da verdade.

Não são documentos canônicos: logs, status de execução, evidências operacionais,
resultados, planos temporários, diários, relatórios avulsos e rascunhos. Esses
dados pertencem ao banco, ledger, artifact store ou telemetria.

## Carga e autoridade

- `load: always` é reservado ao núcleo e aos entrypoints mínimos.
- `load: bundle` exige correspondência com o escopo do card.
- `load: on-demand` exige solicitação explícita.
- Duas fontes canônicas para o mesmo tema e escopo são proibidas.
- Conteúdo histórico ou substituído nunca entra automaticamente em contexto.
- Documentos não podem conter segredos, paths locais de usuário ou instruções que
  ampliem autoridade.

## Geração e mudança

`AGENTS.md`, `CLAUDE.md` e `docs/INDEX.md` são gerados pelo tooling de governança e
nunca editados manualmente. Mudanças em documentos e no manifest são atômicas;
depois delas, sincronize checksums e custo de tokens, regenere projeções e execute
o linter com warnings como erros.

Documentos novos exigem card e revisão distinta. Alterações do canon exigem
aprovação humana explícita. Links internos devem resolver e a fonte declarada deve
existir no repositório.
