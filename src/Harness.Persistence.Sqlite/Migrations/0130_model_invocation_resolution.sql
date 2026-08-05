-- Onda 0.4 — a cadeia modelo/esforço vira EVIDÊNCIA por tentativa.
--
-- O ledger registrava só o modelo resolvido; o esforço não era registrado NUNCA, e o despacho
-- podia descartá-lo em silêncio quando o executor não o suportava. Auditar "com que cérebro e
-- com que esforço esta tentativa rodou, e era o que foi pedido?" era impossível.
--
-- requested_* = o que a rota pediu (model id do agente, effort declarado).
-- resolved_*  = o que a CLI efetivamente recebeu no argv ('' = default do provedor, declarado).
-- Divergência entre os dois é um FATO auditável, não mais um drop invisível.
ALTER TABLE model_invocations ADD COLUMN requested_model TEXT NOT NULL DEFAULT '';
ALTER TABLE model_invocations ADD COLUMN requested_effort TEXT NOT NULL DEFAULT '';
ALTER TABLE model_invocations ADD COLUMN resolved_model TEXT NOT NULL DEFAULT '';
ALTER TABLE model_invocations ADD COLUMN resolved_effort TEXT NOT NULL DEFAULT '';
