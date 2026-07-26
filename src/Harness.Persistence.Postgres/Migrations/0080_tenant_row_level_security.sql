DO $rls$
DECLARE
    tenant_table record;
BEGIN
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
