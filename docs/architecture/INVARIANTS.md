# Invariantes arquiteturais e de domínio

1. Somente Host altera estado de domínio; Runner nunca escreve banco nem executa migrations.
2. Toda transição passa por application service autorizado e transação que inclui auditoria/Outbox quando aplicável.
3. Inbox deduplica `idempotencyKey`; mensagens Runner carregam identidade, tentativa e sequência.
4. Um único chief ativo por `(tenant, projeto)`; fencing rejeita owner antigo.
5. Solicitações, instruções e versões são imutáveis; correção cria nova versão/relação de substituição.
6. Actor-critic é obrigatório a partir de risco médio; produtor não aprova.
7. Progresso executado, validado e aprovado é calculado de itens objetivos e nunca somado.
8. Ledger é append-only e hash-encadeado por tenant.
9. Sequência de evento cresce por stream; lacunas exigem snapshot+delta.
10. SQLite usa dispatcher único, WAL, foreign keys e busy timeout; PostgreSQL usa aquisição concorrente própria.
11. Sandbox não monta home, não recebe segredo e só cria recursos Docker prefixados/labelados.
12. Expiração de licença nunca bloqueia leitura ou exportação de dados.
13. Backend não modifica `frontend/**` ou `docs/frontend/**` durante propriedade da Kimi.
14. Repositório oficial mantém somente `main` e `develop`; merge em `main` requer autorização humana.
