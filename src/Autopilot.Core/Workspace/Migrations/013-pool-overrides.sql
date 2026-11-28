-- Pool-level settings that override the project's autopilot.json values for that project's pool only.
-- A null column means "no override: use the project's value".
CREATE TABLE IF NOT EXISTS pool_overrides (
    project_key         TEXT NOT NULL PRIMARY KEY,
    unattended          INTEGER NULL,
    concurrency         INTEGER NULL,
    master_enabled      INTEGER NULL,
    budget_stop_percent INTEGER NULL,
    integration_enabled INTEGER NULL
);
