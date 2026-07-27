DO $rls$
DECLARE
    tenant_table record;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'poseidon_runtime')
    THEN
        CREATE ROLE poseidon_runtime
            NOLOGIN
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE
            NOINHERIT
            NOREPLICATION
            NOBYPASSRLS;
    ELSE
        ALTER ROLE poseidon_runtime
            NOLOGIN
            NOSUPERUSER
            NOCREATEDB
            NOCREATEROLE
            NOINHERIT
            NOREPLICATION
            NOBYPASSRLS;
    END IF;

    GRANT poseidon_runtime TO CURRENT_USER;
    GRANT USAGE ON SCHEMA harness TO poseidon_runtime;
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA harness TO poseidon_runtime;
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA harness TO poseidon_runtime;
    ALTER DEFAULT PRIVILEGES IN SCHEMA harness
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO poseidon_runtime;
    ALTER DEFAULT PRIVILEGES IN SCHEMA harness
        GRANT USAGE, SELECT ON SEQUENCES TO poseidon_runtime;

    FOR tenant_table IN
        SELECT DISTINCT table_name
        FROM information_schema.columns
        WHERE table_schema = 'harness'
          AND column_name = 'tenant_id'
        ORDER BY table_name
    LOOP
        EXECUTE format(
            'ALTER TABLE harness.%I ENABLE ROW LEVEL SECURITY',
            tenant_table.table_name);
        EXECUTE format(
            'ALTER TABLE harness.%I FORCE ROW LEVEL SECURITY',
            tenant_table.table_name);

        IF NOT EXISTS
        (
            SELECT 1
            FROM pg_policies
            WHERE schemaname = 'harness'
              AND tablename = tenant_table.table_name
              AND policyname = 'tenant_isolation'
        )
        THEN
            EXECUTE format(
                'CREATE POLICY tenant_isolation ON harness.%I ' ||
                'USING (tenant_id::text = current_setting(''poseidon.tenant_id'', true)) ' ||
                'WITH CHECK (tenant_id::text = current_setting(''poseidon.tenant_id'', true))',
                tenant_table.table_name);
        END IF;
    END LOOP;
END
$rls$;
