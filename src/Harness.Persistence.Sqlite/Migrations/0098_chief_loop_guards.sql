-- GUARDAS DE LAÇO DA BRUNA — um agente que planeja e executa o próprio plano pode girar em falso
-- sem cometer nenhum erro visível: cada turno, isolado, parece razoável. O laço só existe na
-- SEQUÊNCIA, e por isso precisa de estado durável — em memória ele desaparece no primeiro reinício,
-- que é justamente quando o laço recomeça do zero.

-- Registro dos turnos que a BRUNA disparou sozinha. Turno pedido pelo dono não entra aqui: as
-- guardas existem contra a autoalimentação dela, nunca contra o usuário.
CREATE TABLE chief_self_triggered_turns
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    project_id TEXT NOT NULL,
    demand_id TEXT NOT NULL,
    demand_plan_id TEXT NULL,
    chief_turn_id TEXT NULL,
    -- Chave causal do que provocou este turno (ex.: 'card:01H…'); nula quando a origem é a própria
    -- demanda. É o que liga o registro ao grafo causal abaixo.
    cause_key TEXT NULL CHECK (cause_key IS NULL OR length(cause_key) <= 200),
    occurred_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

-- Os dois usos são leitura por demanda: acumulado (teto) e recorte recente (taxa por janela).
CREATE INDEX ix_chief_self_triggered_turns_demand
    ON chief_self_triggered_turns (tenant_id, demand_id, occurred_at);

-- ARESTAS CAUSAIS: "cause_key GEROU effect_key". O audit_ledger é encadeado por hash e responde
-- "o que aconteceu, em que ordem, sem adulteração" — não responde "o que causou o quê". Um ciclo
-- causal (A gerou B que regenerou A) é invisível numa sequência temporal: cada evento é legítimo,
-- e só o grafo mostra que a corrente se fechou.
CREATE TABLE chief_causal_edges
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    project_id TEXT NOT NULL,
    cause_key TEXT NOT NULL CHECK (length(cause_key) BETWEEN 1 AND 200),
    effect_key TEXT NOT NULL CHECK (length(effect_key) BETWEEN 1 AND 200),
    -- Por que a aresta existe (ex.: 'plan.materialized', 'card.replanned'): sem isso o grafo diz
    -- que houve causa, mas não de que natureza.
    relation TEXT NOT NULL CHECK (length(relation) BETWEEN 1 AND 100),
    occurred_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, id),
    -- A mesma causa gerando o mesmo efeito pela mesma relação é UM fato, não vários: sem esta
    -- unicidade, reprocessamento infla o grafo e o detector encontra caminhos que não existem.
    UNIQUE (tenant_id, cause_key, effect_key, relation),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

-- O detector caminha da causa para os efeitos; este é o índice do caminhamento.
CREATE INDEX ix_chief_causal_edges_cause
    ON chief_causal_edges (tenant_id, project_id, cause_key);

-- INTERRUPÇÕES: toda guarda que barrou um turno fica registrada com a evidência. Interromper sem
-- dizer por quê é indistinguível de travar — e é o dono que paga a diferença.
CREATE TABLE chief_loop_interruptions
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    project_id TEXT NOT NULL,
    demand_id TEXT NOT NULL,
    demand_plan_id TEXT NULL,
    reason_code TEXT NOT NULL CHECK (reason_code IN (
        'chief_loop.demand_ceiling',
        'chief_loop.plan_reentrancy',
        'chief_loop.rate_window',
        'chief_loop.causal_cycle')),
    detail TEXT NULL CHECK (detail IS NULL OR length(detail) <= 4000),
    -- Caminho do ciclo quando a interrupção foi causal; é a evidência auditável do evento.
    cycle_path_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(cycle_path_json) AND json_type(cycle_path_json) = 'array'),
    occurred_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

CREATE INDEX ix_chief_loop_interruptions_demand
    ON chief_loop_interruptions (tenant_id, demand_id, occurred_at);
