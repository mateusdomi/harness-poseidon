-- Migration 0124: contexto efetivo no recibo de governança.
-- Paridade com o SQLite; ver a migração equivalente para o racional completo.
ALTER TABLE harness.governance_turn_receipts ADD COLUMN context_json jsonb NULL;
