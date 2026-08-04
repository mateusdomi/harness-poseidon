# ADR-0003 — Memória conversacional da Bruna e propriedade do contexto de modelo

## Contexto

A Bruna é a persona de conversação do Poseidon. O prompt de cada turno é montado a
partir de:

- persona, governança e política de comunicação (autoridade);
- `StatusDigestJson` e catálogo de especialistas (dados);
- mensagens do usuário e da Bruna, lidas de `conversation_messages`;
- estado do chefe em `chief_states`, `chief_turn_intents`, `chief_turn_blocks`,
  `chief_causal_edges` e `chief_loop_interruptions`.

A continuidade da *conversa* — o que foi dito, por quem, em que ordem — vive no
banco do produto e é recuperável a qualquer momento.

A continuidade do *modelo*, no entanto, depende hoje do `--resume <sessionId>` da
CLI do fornecedor. A CLI do Claude Code mantém, fora do Poseidon, o estado de
sessão que permite que o modelo "lembre" do tom, do contexto imediato e de
instruções anteriores sem que tudo seja reenviado a cada turno. Esse `SessionId`
não é um identificador do produto: é gerenciado pela CLI do fornecedor.

O achado `F-08` da auditoria macro apontou o risco: trocar de provedor perde o
histórico do modelo, mesmo que o histórico da conversa em `conversation_messages`
sobreviva.

## Decisão

**O contexto de modelo pertence ao fornecedor; o histórico da conversa pertence
ao produto.**

1. O Poseidon mantém a fonte da verdade conversacional em `conversations` e
   `conversation_messages`. Nenhuma troca de provedor apaga esse registro.
2. O Poseidon mantém o estado estruturado do chefe (`chief_states` e tabelas
   afins) e as notas de contexto (`chief_context_notes`) como memória própria,
   independente de fornecedor.
3. A troca de provedor (ou a perda do `SessionId` da CLI) inicia um novo contexto
   de modelo. A Bruna pode reconstruir contexto a partir das mensagens
   persistidas e do estado do chefe, mas o modelo não carrega automaticamente a
   memória de sessão anterior do fornecedor anterior.
4. Não implementamos um adaptador de sessão de modelo entre fornecedores nesta
   versão. O custo de normalizar formato, semântica de system prompt e
   compactação de histórico entre CLIs diferentes é maior que o retorno
   imediato, enquanto o modo pessoal opera com um único fornecedor ativo por
   vez.

## Consequências

- **Troca de provedor = perda de contexto de modelo, não de conversa.** O dono
  vê a continuidade factual preservada, mas pode notar que o tom ou o contexto
  imediato mudou.
- **A memória durável do produto é a autoridade.** Recuperação determinística
  (`ContextBundleBuilder`), estado do chefe e `conversation_messages` são o que
  a Bruna carrega para reconstruir o quadro.
- **Risco consciente aceito:** em um cenário de failover rápido entre contas do
  mesmo fornecedor, o `SessionId` geralmente pode ser reaproveitado; entre
  fornecedores diferentes, não.
- **Trigger para revisão:** se o Poseidon passar a operar com múltiplos
  fornecedores simultâneos no caminho de conversação, ou se vender como serviço
  onde a troca de fornecedor é transparente, este ADR deve ser revisitado para
  decidir se vale construir um adaptador de sessão próprio.

## Alternativas descartadas

- **Reconstruir o histórico de modelo a partir de `conversation_messages` a cada
  turno.** Tecnicamente possível, mas aumenta o custo de tokens, o tempo de
  resposta e a chance de truncamento. A CLI já faz compactação de sessão de
  forma otimizada para o modelo.
- **Persistir o `SessionId` e o estado interno da CLI no banco do produto.**
  Fragiliza o desacoplamento: o formato é opaco, depende de versão da CLI e
  viola a fronteira do fornecedor.

## Relacionado

- Achado `F-08` da auditoria macro de 2026-08-03.
- `docs/backend/memory.md` — camadas de memória do produto.
- `docs/decisions/ADR-0001-arquitetura-definitiva-v3.md` — decisão de
  CLI-first em worktrees.
