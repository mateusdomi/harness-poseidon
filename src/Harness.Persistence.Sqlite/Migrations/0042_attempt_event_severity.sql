ALTER TABLE attempt_events ADD COLUMN severity TEXT NOT NULL DEFAULT 'info'
    CHECK(severity IN ('info','warning','error','critical'));

CREATE INDEX ix_attempt_events_severity
    ON attempt_events (tenant_id,severity,occurred_at,id);
