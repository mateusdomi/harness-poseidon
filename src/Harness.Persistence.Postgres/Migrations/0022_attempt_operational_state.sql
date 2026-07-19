ALTER TABLE harness.work_attempts ADD COLUMN operational_state varchar(20) NOT NULL DEFAULT 'queued'
    CHECK (operational_state IN ('queued', 'running', 'completed', 'failed', 'cancelled'));

UPDATE harness.work_attempts
SET operational_state = CASE state
    WHEN 'running' THEN 'running'
    WHEN 'rejected' THEN 'failed'
    ELSE 'completed'
END;

CREATE INDEX ix_work_attempts_operational_state
    ON harness.work_attempts (tenant_id, project_id, operational_state, id);
