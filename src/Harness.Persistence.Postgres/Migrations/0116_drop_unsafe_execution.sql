-- Paridade com o SQLite: o aceite de modo inseguro deixa de existir no código e nos DADOS (0-E,
-- decisão do proprietário em 31/07/2026). O contêiner é pré-requisito nos dois modos.
DROP TABLE IF EXISTS harness.unsafe_execution_acceptances;

UPDATE harness.profile_settings SET unsafe_mode_accepted_at = NULL
WHERE unsafe_mode_accepted_at IS NOT NULL;
