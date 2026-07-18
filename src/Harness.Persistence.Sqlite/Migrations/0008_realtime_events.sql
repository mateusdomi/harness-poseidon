CREATE TABLE realtime_streams
(
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    stream_name TEXT NOT NULL UNIQUE
        CHECK (length(stream_name) BETWEEN 3 AND 200 AND instr(stream_name, ':') > 1),
    last_sequence INTEGER NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, stream_name)
);

CREATE TABLE realtime_events
(
    message_id TEXT PRIMARY KEY CHECK (length(message_id) = 26),
    tenant_id TEXT NOT NULL,
    stream_name TEXT NOT NULL,
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    event_type TEXT NOT NULL CHECK (length(event_type) BETWEEN 1 AND 200),
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    occurred_at TEXT NOT NULL,
    FOREIGN KEY (tenant_id, stream_name)
        REFERENCES realtime_streams(tenant_id, stream_name),
    UNIQUE (tenant_id, stream_name, sequence)
);

CREATE INDEX ix_realtime_events_stream_sequence
    ON realtime_events (stream_name, sequence);
CREATE INDEX ix_realtime_events_stream_type_sequence
    ON realtime_events (stream_name, event_type, sequence DESC);

CREATE TRIGGER tr_realtime_events_no_update
BEFORE UPDATE ON realtime_events
BEGIN
    SELECT RAISE(ABORT, 'realtime events are append-only');
END;

CREATE TRIGGER tr_realtime_events_no_delete
BEFORE DELETE ON realtime_events
BEGIN
    SELECT RAISE(ABORT, 'realtime events are append-only');
END;
