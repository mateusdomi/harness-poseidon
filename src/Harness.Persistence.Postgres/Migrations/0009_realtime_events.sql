CREATE TABLE harness.realtime_streams
(
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    stream_name varchar(200) NOT NULL UNIQUE
        CHECK (position(':' in stream_name) > 1 AND stream_name !~ '[[:space:]]'),
    last_sequence bigint NOT NULL DEFAULT 0 CHECK (last_sequence >= 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, stream_name)
);

CREATE TABLE harness.realtime_events
(
    message_id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    stream_name varchar(200) NOT NULL,
    sequence bigint NOT NULL CHECK (sequence > 0),
    event_type varchar(200) NOT NULL CHECK (length(trim(event_type)) > 0),
    payload_json jsonb NOT NULL CHECK (jsonb_typeof(payload_json) = 'object'),
    occurred_at timestamptz NOT NULL,
    FOREIGN KEY (tenant_id, stream_name)
        REFERENCES harness.realtime_streams(tenant_id, stream_name),
    UNIQUE (tenant_id, stream_name, sequence)
);

CREATE INDEX ix_realtime_events_stream_sequence
    ON harness.realtime_events (stream_name, sequence);
CREATE INDEX ix_realtime_events_stream_type_sequence
    ON harness.realtime_events (stream_name, event_type, sequence DESC);

CREATE OR REPLACE FUNCTION harness.reject_realtime_event_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'realtime events are append-only' USING ERRCODE = '23000';
END;
$$;

CREATE TRIGGER tr_realtime_events_no_mutation
BEFORE UPDATE OR DELETE ON harness.realtime_events
FOR EACH ROW EXECUTE FUNCTION harness.reject_realtime_event_mutation();
