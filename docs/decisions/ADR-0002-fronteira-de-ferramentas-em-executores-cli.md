# ADR-0002 — Onde a política de ferramentas é aplicada em executores CLI

## Contexto

`governance/core.md` exige que toda delegação nasça de um card com **ferramentas
permitidas e negadas**. O `OPS-015` registrou que o `ToolCallBroker` está declarado no
contêiner de injeção e nunca é resolvido em produção — só testes o usam — e concluiu que a
regra não teria correspondente estrutural.

A conclusão estava parcialmente errada, e a parte errada importa: existe aplicação de
política de ferramentas em produção. O que não existe é interceptação **por chamada**.

O caminho real de execução é uma CLI externa (Claude Code, Codex, GLM, Antigravity) hospedada
como subprocesso dentro de um contêiner. Quem decide invocar uma ferramenta é a CLI, dentro do
contêiner, sem passar pelo processo do Poseidon. Um broker in-process só poderia interceptar
chamadas que atravessassem o Host — e nenhuma atravessa. Ligá-lo ao caminho atual não
produziria enforcement: produziria a *aparência* de enforcement, que é pior que a ausência
dele, porque um controle que não controla ainda aparece verde no relatório.

## Decisão

A fronteira de ferramentas dos executores CLI é o **contêiner**, e a autorização é **por
tentativa**, não por chamada. Três controles compõem a regra e todos já são executados antes
do `Accepted` de um run (`AgentRunOrchestrator`):

1. **Allowlist da persona.** `RequiredToolIds` vem do card/persona. Sem ele resolvido, a
   tentativa é negada com `persona_tools_unresolved` — Default-FAIL, como manda o
   `governance/core.md`.
2. **Política por ferramenta.** Cada ferramenta é conferida no catálogo e avaliada por
   `ToolExecutionPolicy` contra o papel, o risco e a allowlist. Ferramenta desconhecida
   nega a tentativa (`tool_not_found`).
3. **Contenção atestada.** A avaliação exige uma `SandboxAttestation` **verificada e do
   próprio attempt** — uma attestation de outra tentativa não vale, senão uma execução
   isolada no passado liberaria as seguintes.

Fora da fronteira de ferramentas, o alcance de escrita é limitado por `ScopeClaims` por card
e pela worktree isolada; o contêiner entra com rootfs somente-leitura e egresso por proxy.

O `ToolCallBroker` **permanece** no código, sem consumidor de produção, para o caminho de
ferramentas in-process (MCP hospedado pelo Poseidon) previsto no ADR-0001. Ele não é código
morto por engano: é a metade in-process de uma fronteira cuja metade atual é o contêiner.

## Consequências

- A regra do `governance/core.md` tem correspondente estrutural, e ele é verificável: a
  negação acontece antes da aceitação do run e é registrada com código tipado.
- **Limite aceito conscientemente:** o Poseidon não observa, não registra e não nega
  ferramenta *individual* invocada por uma CLI dentro do contêiner. O que ele garante é o
  conjunto autorizado, a contenção e o alcance de escrita. Quem precisar de trilha por
  chamada precisa de executor in-process, não de configuração.
- Se um executor in-process for adotado, o `ToolCallBroker` passa a ter consumidor e este ADR
  deve ser revisitado — não substituído: os três controles por tentativa continuam valendo.

## Alternativas descartadas

- **Ligar o broker ao caminho atual.** Não intercepta nada, porque as chamadas não passam
  pelo Host. Custo real, benefício zero, e o pior efeito colateral possível: um controle
  decorativo marcado como implementado.
- **Proibir executores CLI até haver interceptação por chamada.** Removeria o produto para
  proteger um requisito que a contenção já atende no nível que importa.
