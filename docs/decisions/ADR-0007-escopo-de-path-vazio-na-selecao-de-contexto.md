# ADR-0007 — Escopo de path vazio na seleção de contexto

## Contexto

`ContextBundleBuilder.MatchesPaths` decide se um documento do manifesto se aplica a um pedido de
contexto, cruzando os `pathGlobs` declarados pelo documento com os `paths` declarados pelo pedido
(os *scope claims* da tentativa). A regra atual é:

```csharp
globs.Contains("**") || paths.Count == 0 || paths.Any(path => globs.Any(glob => …))
```

O `paths.Count == 0` no meio significa: **pedido sem escopo de arquivo casa com todos os
documentos**, inclusive os que restringem a si mesmos a subárvores específicas.

Medido no manifesto atual: dos 37 documentos carregáveis (`always`/`bundle`), **14 declaram
`pathGlobs: ['**']`** (universais) e **23 restringem-se a caminhos** — somando 11.008 tokens.

Quem pede contexto sem escopo de arquivo é o **turno de conversa da chefe**: ela não adquire
ScopeClaim, não abre worktree e não toca em arquivo nenhum. Hoje ela recebe, entre outros,
`backend-memory`, `backend-context` e `backend-observability` — documentos endereçados a quem
trabalha dentro de `src/Modules/Harness.Modules.Governance/**`. Um turno que não toca em arquivo
não está trabalhando nesses módulos.

Este comportamento foi identificado na Fase 2 e deliberadamente adiado: é uma mudança semântica
transversal, e mudar primeiro para documentar depois seria exatamente o oposto do que o núcleo de
governança exige.

## Opções

**A — `[] = casa tudo` (comportamento atual).** Simples e permissiva. Um pedido sem escopo recebe
o canon inteiro, incluindo documentos sobre código que ele não vai tocar. Custa orçamento de
contexto e dilui o que importa: na medição da Fase 2, o bundle da chefe chegou a 16.547 tokens, e
foi preciso elevar o teto para 24.000 só para acomodar documentos que não lhe dizem respeito.

**B — `[] = casa nada.`** Um pedido sem escopo perderia TODOS os documentos, inclusive os
universais e o próprio núcleo de governança. Inaceitável: transformaria ausência de escopo em
ausência de governança.

**C — `[] = ausência de restrição de path`, com a seleção decidida pelas demais dimensões.** É o
que a implementação atual FAZ (idêntica a A na prática), e a formulação esconde a pergunta em vez
de respondê-la: se não há restrição, por que um documento que se restringe a `infra/sandbox/**`
deveria entrar?

**D — Simetria: o pedido declara onde trabalha; o documento declara onde se aplica.** Pedido sem
paths não trabalha em lugar nenhum, então documento que se restringe a caminhos não se aplica a
ele. Documento universal (`**`) continua se aplicando sempre.

## Decisão

**Adotada a opção D.** A dimensão `pathGlobs` passa a ser simétrica:

| Pedido | Documento | Resultado |
|---|---|---|
| `paths` não vazio | `pathGlobs: ['**']` | aplica |
| `paths` não vazio | restrito, com interseção | aplica |
| `paths` não vazio | restrito, sem interseção | não aplica |
| **`paths` vazio** | `pathGlobs: ['**']` | **aplica** |
| **`paths` vazio** | **restrito** | **não aplica** |

Um documento que quer alcançar trabalho sem escopo de arquivo declara `pathGlobs: ['**']` — que é
o que ele sempre significou: "vale para qualquer trabalho".

## Consequências

- O turno da chefe deixa de receber 23 documentos endereçados a subárvores de código que ele não
  toca, liberando orçamento para a memória recuperada PARA aquele turno — que era o que caía
  primeiro quando o bundle apertava.
- Documentos endereçados a um agente ou papel específico, e não a um caminho, precisam declarar
  `pathGlobs: ['**']`. Foi o caso de `agent-bruna`, corrigido nesta mudança: a persona da chefe é
  endereçada por `agents: [chief-orchestrator]`, e restringi-la a `src/Harness.Host/Workers/**`
  era dizer que ela só vale quando alguém edita aqueles arquivos.
- Execuções de agente **não mudam**: elas sempre declaram scope claims, e o ramo `paths.Count == 0`
  nunca era alcançado por elas.
- Risco residual: um documento que hoje depende do casamento permissivo para chegar a um pedido sem
  escopo deixa de chegar. A varredura do manifesto identificou um único caso (`agent-bruna`), já
  corrigido. O linter de governança e os testes de seleção por dimensão protegem o resto.

## Alternativas descartadas

- **Manter A e resolver por orçamento.** Foi o que a Fase 4 fez ao elevar o teto para 24.000.
  Trata o sintoma: o bundle continua carregando documentos irrelevantes, e o teto teria de subir de
  novo a cada documento novo do canon.
- **B.** Ausência de escopo viraria ausência de governança.
- **Introduzir um seletor novo (`appliesToScopelessWork`).** Uma dimensão a mais para expressar o
  que `pathGlobs: ['**']` já expressa. O manifesto já tem sete dimensões de seleção; a oitava
  precisaria pagar por si.

## Relacionado

- `src/Modules/Harness.Modules.Governance/Context/ContextBundleBuilder.cs` — `MatchesPaths`.
- `governance/manifest.yaml` — `pathGlobs` de cada documento.
- [ADR-0006](ADR-0006-baseline-de-engenharia-do-produto-entregue.md) — o canon de produto, cujos
  documentos já nasceram com `pathGlobs: ['**']` por causa deste mesmo comportamento.
