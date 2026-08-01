-- 0-E (decisão do proprietário, 31/07/2026): o contêiner é PRÉ-REQUISITO nos dois modos. O aceite
-- de modo inseguro deixa de existir — no código e nos DADOS.
--
-- Uma exceção "temporária" que o produto aceita vira permanente na prática. Enquanto a linha de
-- aceite existisse no banco, um upgrade futuro poderia voltar a lê-la e reabrir silenciosamente o
-- caminho que executa agente no host sem fronteira nenhuma. Por isso não basta parar de escrever:
-- o dado é removido.
DROP TABLE IF EXISTS unsafe_execution_acceptances;

-- O aceite antigo, por PERFIL, que liberava execução local de projeto. Mesma decisão: o valor é
-- zerado para que nenhuma leitura remanescente encontre um aceite vivo.
UPDATE profile_settings SET unsafe_mode_accepted_at = NULL WHERE unsafe_mode_accepted_at IS NOT NULL;
