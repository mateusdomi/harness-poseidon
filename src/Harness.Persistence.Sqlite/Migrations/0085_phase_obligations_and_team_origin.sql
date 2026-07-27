-- Obrigações REAIS de uma fase: a unidade de que o progresso é feito.
-- Antes disto, o progresso da esteira era medido pelos documentos previstos — o que em fases de
-- execução permitiria fechar a fase produzindo briefing, code review e métricas sem uma linha de
-- código implementada. Uma obrigação pode ser documento, implementação, teste, revisão, evidência,
-- métrica, decisão ou operação de release; documento conclui apenas a própria obrigação.
CREATE TABLE phase_obligations
(
    tenant_id TEXT NOT NULL,
    obligation_id TEXT NOT NULL CHECK (length(obligation_id) = 26),
    project_id TEXT NOT NULL,
    run_id TEXT NOT NULL,
    phase_key TEXT NOT NULL CHECK (length(phase_key) BETWEEN 1 AND 200),
    obligation_key TEXT NOT NULL CHECK (length(obligation_key) BETWEEN 1 AND 200),
    plan_version INTEGER NOT NULL CHECK (plan_version >= 1),
    kind TEXT NOT NULL CHECK (kind IN
        ('document','implementation','test','review','evidence','metric','decision','operation')),
    description TEXT NOT NULL CHECK (length(description) BETWEEN 1 AND 2000),
    required INTEGER NOT NULL DEFAULT 1 CHECK (required IN (0,1)),
    weight REAL NOT NULL DEFAULT 1 CHECK (weight >= 0),
    -- Quem introduziu a obrigação: o template da fase, o plano da demanda, a chefe ou uma policy.
    source TEXT NOT NULL CHECK (source IN ('template','plan','chief','policy')),
    completion_criteria TEXT NULL CHECK (completion_criteria IS NULL OR length(completion_criteria) <= 2000),
    card_id TEXT NULL,
    objective_key TEXT NULL,
    artifact_ref TEXT NULL,
    state TEXT NOT NULL DEFAULT 'pending' CHECK (state IN
        ('pending','in_progress','in_review','blocked','accepted','cancelled')),
    evidence_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(evidence_json) AND json_type(evidence_json) = 'array'),
    -- Justificativa de criação fora do template e, quando cancelada, o porquê. Cancelar sem
    -- motivo registrado é o caminho óbvio para fabricar 100% removendo o que falhou.
    reason TEXT NULL CHECK (reason IS NULL OR length(reason) <= 2000),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, obligation_id),
    UNIQUE (tenant_id, run_id, phase_key, plan_version, obligation_key)
);

CREATE INDEX ix_phase_obligations_phase
    ON phase_obligations (tenant_id, run_id, phase_key, plan_version);

CREATE INDEX ix_phase_obligations_card
    ON phase_obligations (tenant_id, card_id);

-- PROCEDÊNCIA e CICLO DE VIDA das personas. A chefe passa a criar especialistas sozinha quando há
-- lacuna real, então o catálogo precisa registrar QUEM criou, POR QUÊ, em que escopo e em que
-- estágio de confiança o perfil está — sem isso a auditoria humana não tem o que auditar.
ALTER TABLE agent_definitions ADD COLUMN origin TEXT NOT NULL DEFAULT 'human'
    CHECK (origin IN ('human','chief','system'));

ALTER TABLE agent_definitions ADD COLUMN lifecycle_state TEXT NOT NULL DEFAULT 'active'
    CHECK (lifecycle_state IN ('project_scoped','active','reusable','global','observation','quarantined','disabled'));

-- Projeto que motivou a criação. Persona nova nasce limitada a ele; a ampliação de alcance é uma
-- promoção auditada, nunca o estado inicial.
ALTER TABLE agent_definitions ADD COLUMN scope_project_id TEXT NULL;

ALTER TABLE agent_definitions ADD COLUMN creation_reason TEXT NULL
    CHECK (creation_reason IS NULL OR length(creation_reason) <= 2000);

CREATE INDEX ix_agent_definitions_origin
    ON agent_definitions (tenant_id, origin, lifecycle_state);
