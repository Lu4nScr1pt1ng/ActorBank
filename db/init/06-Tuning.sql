-- ActorBank tuning. Safe and Orleans-schema-preserving (storage params only — no column,
-- type, or query changes). The storage table is UPDATEd in place on every grain state write,
-- so leave free space per page for HOT (heap-only-tuple) updates and vacuum it more eagerly.
ALTER TABLE OrleansStorage SET (
    fillfactor = 85,
    autovacuum_vacuum_scale_factor = 0.05,
    autovacuum_analyze_scale_factor = 0.02
);

-- Reminders are also updated frequently (IAmAlive-style heartbeats on the schedule).
ALTER TABLE OrleansRemindersTable SET (fillfactor = 85);
