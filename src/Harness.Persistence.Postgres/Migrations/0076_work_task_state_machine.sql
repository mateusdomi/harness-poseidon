ALTER TABLE harness.work_tasks
    DROP CONSTRAINT work_tasks_state_check;

ALTER TABLE harness.work_tasks
    ADD CONSTRAINT work_tasks_state_check CHECK
    (
        state IN
        (
            'draft',
            'triaged',
            'ready',
            'assigned',
            'running',
            'review',
            'awaiting_review',
            'blocked',
            'approved',
            'escalated',
            'merged',
            'done',
            'completed',
            'cancelled'
        )
    );
