-- Shadow records: what Autopilot would have decided next to what the person decided. Append only.
CREATE TABLE IF NOT EXISTS shadow_records (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    project_key      TEXT NOT NULL,
    task_or_run      TEXT NOT NULL,
    decision_type    TEXT NOT NULL,
    autopilot_verdict TEXT NOT NULL,
    person_verdict   TEXT NOT NULL,
    at               TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_shadow_records_project ON shadow_records (project_key, decision_type);
