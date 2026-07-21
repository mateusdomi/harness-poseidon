-- ADR-018: definições built-in não referenciam modelos fictícios. O catálogo simulado deixa de
-- ser semeado em instalações normais, portanto estas referências passam a não resolver: elas são
-- limpas para NULL (não resolvido até o usuário configurar), com prontidão explícita (ADR-017).
-- Idempotente: só afeta os IDs do seed de conveniência da RC3.
UPDATE harness.agent_definitions
   SET default_model_id = NULL
 WHERE default_model_id IN ('01ARZ3NDEKTSV4RRFFQ69G5FJ1', '01ARZ3NDEKTSV4RRFFQ69G5FJ2');
