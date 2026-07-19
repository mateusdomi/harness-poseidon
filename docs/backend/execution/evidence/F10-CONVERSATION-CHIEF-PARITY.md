# Evidência F10-3 — paridade PostgreSQL de conversas e pipeline do Chief

Data: 2026-07-19.

Terceira onda da paridade PostgreSQL — o coração conversacional do produto:

- **Migrations PG 0018–0019** (`conversations`, `chief_turn_pipeline`): transcrição fiel das SQLite 0012/0027 com CHECKs de coerência de autor, lease e FKs compostas; `last_digest_json` como `jsonb`.
- **`PostgresConversationStore` (+ parcial ChiefTurns) e `PostgresChiefOrchestratorStore`**: chat completo (criação com projeto ativo, mensagens, turno Fake transacional com a MESMA ordem de eventos/ticks), pipeline durável do Chief — Enqueue com replay por turn id sob advisory lock, `AcquireNext` com `FOR UPDATE ... SKIP LOCKED` substituindo o writer único do SQLite, fencing crescente, Complete transacional com `chat_turns` + eventos + **materialização de demandas**, Fail com retry limitado — e os comandos do orquestrador (pause/resume/handoff com validação de definição/modelo, drain atômico).
- **Correção de paridade real**: a criação de projeto no PG agora insere o agente Chief na mesma transação (omissão da onda 1 detectada pela FK de `chief_states` — o schema forçou a correção do comportamento, exatamente como projetado).
- **`ConversationChiefStoreBehavior`** provider-neutro: conversa criada (CHECK `version > 0` do schema pegou um comando inválido do próprio cenário — corrigido); enqueue + replay pelo mesmo turn id sem duplicar mensagem; lease adquirida; conclusão materializa exatamente uma demanda e appenda a resposta do Chief; fila drenada (`AcquireNext` nulo); lease antigo não conclui duas vezes (`ChiefTurnConflictException`). Verde no SQLite e na bateria PostgreSQL do container gerenciado (**19 migrations reais**).

Lacunas conscientes (próximas ondas): colunas de projeção do quadro em `solicitations`/`demands` PG (0013 SQLite) — a materialização PG grava o subconjunto existente com payloads de evento idênticos; `work_tasks.board_state` e `work_attempts.operational_state` adaptados no drain até as ondas 0013/0019.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 220/220; PG `19→0`; zero Docker órfão.
