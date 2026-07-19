ALTER TABLE harness.attempt_events ADD COLUMN severity varchar(20) NOT NULL DEFAULT 'info'
    CHECK(severity IN ('info','warning','error','critical'));

CREATE INDEX ix_attempt_events_severity
    ON harness.attempt_events (tenant_id,severity,occurred_at,id);
