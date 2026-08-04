-- Migration 0124: contexto efetivo no recibo de governança.
--
-- O recibo já registrava QUAIS documentos entraram; não registrava sob qual persona, papel,
-- workflow, fase, tipo de card ou baseline a execução aconteceu, nem por que um item selecionado
-- ficou de fora. Sem isso, "a regra existia mas não foi carregada" e "a regra foi carregada e o
-- agente violou" produzem exatamente o mesmo registro — e a investigação termina em "o agente
-- fez errado".
--
-- A coluna é anulável de propósito: recibos anteriores a esta migração não têm o dado, e inventar
-- valor para satisfazer o leitor transformaria ausência em fato errado.
ALTER TABLE governance_turn_receipts ADD COLUMN context_json TEXT NULL
    CHECK (context_json IS NULL OR json_valid(context_json));
