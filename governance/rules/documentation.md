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

## Descoberta antes da criação

Antes de criar qualquer documento, o agente executa **inspecionar, reconciliar, criar,
indexar**:

1. **Inspecionar** — procurar o nome canônico, aliases, conteúdo semanticamente
   equivalente, referências no índice e documentação dentro do módulo afetado.
2. **Reconciliar** — existindo documento equivalente, lê-lo, comparar, preservar as
   decisões válidas, complementar as lacunas, corrigir a inconsistência e atualizar as
   referências. Não criar um segundo arquivo com nome diferente sobre o mesmo assunto.
3. **Criar** — somente quando a responsabilidade não estiver coberta por nenhum
   documento canônico existente.
4. **Indexar** — registrar no manifest no mesmo commit, conforme a seção anterior.

Uma família de arquivos dizendo quase a mesma coisa — padrões de código, diretrizes de
desenvolvimento, boas práticas, regras de qualidade — é documentação ruim, não
documentação abundante. **Número de documentos não é indicador de qualidade.**

Quando duas regras ativas conflitarem, marcar o conflito e aplicar a precedência do
núcleo se ela for inequívoca; havendo ambiguidade real, abrir decisão. Nunca escolher
a regra mais conveniente para fechar o card.

## Escopo global e escopo de projeto

Padrão genérico vive no canon global — `governance/` para o repositório e
`docs/product/` para o produto entregue. Um projeto **não copia** o canon para dentro
de si: a cópia diverge do original na primeira evolução.

O projeto declara apenas o que é dele — perfil efetivo, overrides, ADRs, documentação
de domínio, arquitetura específica e runbook — e herda o resto por referência.

## Geração e mudança

`AGENTS.md`, `CLAUDE.md` e `docs/INDEX.md` são gerados pelo tooling de governança e
nunca editados manualmente. Mudanças em documentos e no manifest são atômicas;
depois delas, sincronize checksums e custo de tokens, regenere projeções e execute
o linter com warnings como erros.

Documentos novos exigem card e revisão distinta. Alterações do canon exigem
aprovação humana explícita. Links internos devem resolver e a fonte declarada deve
existir no repositório.
