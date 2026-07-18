CREATE FUNCTION harness.reject_audit_ledger_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'audit_ledger is append-only';
END;
$$;

CREATE TRIGGER audit_ledger_no_mutation
BEFORE UPDATE OR DELETE ON harness.audit_ledger
FOR EACH ROW EXECUTE FUNCTION harness.reject_audit_ledger_mutation();
