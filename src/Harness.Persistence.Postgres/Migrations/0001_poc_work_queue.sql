CREATE SCHEMA IF NOT EXISTS harness_poc;

CREATE TABLE IF NOT EXISTS harness_poc.work_items
(
    id text PRIMARY KEY,
    payload jsonb NOT NULL,
    state text NOT NULL CHECK (state IN ('ready', 'leased', 'completed')),
    owner_id text NULL,
    lease_token bigint NOT NULL DEFAULT 0,
    lease_expires_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_work_items_acquisition
    ON harness_poc.work_items (state, created_at, id)
    WHERE state = 'ready';
