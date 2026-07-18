ALTER TABLE harness.realtime_streams
    DROP CONSTRAINT realtime_streams_stream_name_check;

ALTER TABLE harness.realtime_streams
    ADD CONSTRAINT realtime_streams_stream_name_check
    CHECK (
        (stream_name = 'global' OR position(':' in stream_name) > 1)
        AND stream_name !~ '[[:space:]]');
