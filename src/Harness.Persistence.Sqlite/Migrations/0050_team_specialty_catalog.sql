-- CAT-04: catálogos reais e tenant-scoped de equipe (team) e especialidade (specialty).
-- Antes, `team` e `specialty` eram texto livre em agent_definitions. Agora viram tabelas de
-- catálogo com chave estável (slug) e nome único por tenant; uma especialidade pode,
-- opcionalmente, pertencer a uma equipe. As definições passam a referenciar entradas
-- VÁLIDAS do catálogo (validação no store), e apagar uma entrada referenciada falha com
-- conflito tipado.
--
-- COMPATIBILIDADE: este passo SEMEIA o catálogo a partir dos valores DISTINTOS já presentes
-- em agent_definitions (por tenant), de modo que toda referência existente continue válida.
-- Definições de sistema (tenant_id IS NULL) não são semeadas nem revalidadas.

CREATE TABLE teams
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    team_key TEXT NOT NULL CHECK (length(team_key) BETWEEN 1 AND 100),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    description TEXT NULL CHECK (description IS NULL OR length(description) <= 2000),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, team_key),
    UNIQUE (tenant_id, name)
);

CREATE INDEX ix_teams_tenant ON teams (tenant_id, id);

CREATE TABLE specialties
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    specialty_key TEXT NOT NULL CHECK (length(specialty_key) BETWEEN 1 AND 100),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    description TEXT NULL CHECK (description IS NULL OR length(description) <= 2000),
    team_id TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, specialty_key),
    UNIQUE (tenant_id, name),
    FOREIGN KEY (tenant_id, team_id) REFERENCES teams (tenant_id, id)
);

CREATE INDEX ix_specialties_tenant ON specialties (tenant_id, id);
CREATE INDEX ix_specialties_team ON specialties (tenant_id, team_id, id);

-- Seed determinístico por tenant a partir dos times distintos já referenciados. O id é um
-- ULID léxico válido (26 chars do alfabeto Crockford; hex é subconjunto) com primeiro
-- caractere '0' para caber nos 128 bits. A chave é o slug do nome.
INSERT INTO teams (id, tenant_id, team_key, name, description, created_at, updated_at)
SELECT
    '0' || substr(hex(randomblob(13)), 2, 25),
    d.tenant_id,
    lower(replace(replace(replace(d.team, ' ', '-'), '/', '-'), '_', '-')),
    d.team,
    NULL,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM (
    SELECT DISTINCT tenant_id, trim(team) AS team
    FROM agent_definitions
    WHERE tenant_id IS NOT NULL AND team IS NOT NULL AND trim(team) <> ''
) d;

INSERT INTO specialties (id, tenant_id, specialty_key, name, description, team_id, created_at, updated_at)
SELECT
    '0' || substr(hex(randomblob(13)), 2, 25),
    d.tenant_id,
    lower(replace(replace(replace(d.specialty, ' ', '-'), '/', '-'), '_', '-')),
    d.specialty,
    NULL,
    NULL,
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM (
    SELECT DISTINCT tenant_id, trim(specialty) AS specialty
    FROM agent_definitions
    WHERE tenant_id IS NOT NULL AND specialty IS NOT NULL AND trim(specialty) <> ''
) d;
