CREATE TRIGGER audit_ledger_no_update
BEFORE UPDATE ON audit_ledger
BEGIN
    SELECT RAISE(ABORT, 'audit_ledger is append-only');
END;

CREATE TRIGGER audit_ledger_no_delete
BEFORE DELETE ON audit_ledger
BEGIN
    SELECT RAISE(ABORT, 'audit_ledger is append-only');
END;
