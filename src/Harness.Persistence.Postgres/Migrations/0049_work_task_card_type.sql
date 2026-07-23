-- Tipo do card na esteira de trabalho. FAIL-SAFE por design: só 'agent_task' é auto-despachável
-- pelo loop autônomo do Chefe. 'human_gate' e 'decision' exigem um humano; 'feature' e 'spike'
-- são portadores de escopo/investigação que NUNCA devem virar execução de agente sozinhos.
-- O default 'agent_task' preserva o comportamento dos cards existentes (a esteira só os enxerga
-- após a triagem promovê-los a 'ready'), enquanto o CHECK impede valores fora do conjunto fechado.
ALTER TABLE harness.work_tasks ADD COLUMN card_type text NOT NULL DEFAULT 'agent_task'
    CHECK (card_type IN ('feature','agent_task','human_gate','spike','decision'));
