-- CAT-04: catálogos reais e tenant-scoped de equipe (team) e especialidade (specialty).
-- Antes, `team` e `specialty` eram texto livre em harness.agent_definitions. Agora viram
-- tabelas de catálogo com chave estável (slug) e nome único por tenant; uma especialidade
-- pode, opcionalmente, pertencer a uma equipe. As definições passam a referenciar entradas
-- VÁLIDAS do catálogo (validação no store), e apagar uma entrada referenciada falha com
-- conflito tipado.
--
-- COMPATIBILIDADE: este passo SEMEIA o catálogo a partir dos valores DISTINTOS já presentes
-- em agent_definitions (por tenant), de modo que toda referência existente continue válida.
-- Definições de sistema (tenant_id IS NULL) não são semeadas nem revalidadas.

CREATE TABLE harness.teams
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    team_key varchar(100) NOT NULL,
    name varchar(200) NOT NULL,
    description varchar(2000) NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, team_key),
    UNIQUE (tenant_id, name)
);

CREATE INDEX ix_teams_tenant ON harness.teams (tenant_id, id);

CREATE TABLE harness.specialties
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    specialty_key varchar(100) NOT NULL,
    name varchar(200) NOT NULL,
    description varchar(2000) NULL,
    team_id char(26) NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, specialty_key),
    UNIQUE (tenant_id, name),
    FOREIGN KEY (tenant_id, team_id) REFERENCES harness.teams (tenant_id, id)
);

CREATE INDEX ix_specialties_tenant ON harness.specialties (tenant_id, id);
CREATE INDEX ix_specialties_team ON harness.specialties (tenant_id, team_id, id);

-- Seed por tenant a partir dos times distintos já referenciados. O id é um ULID léxico
-- válido (26 chars do alfabeto Crockford; hex é subconjunto) com primeiro caractere '0'
-- para caber nos 128 bits. A chave é o slug do nome.
INSERT INTO harness.teams (id, tenant_id, team_key, name, description, created_at, updated_at)
SELECT
    '0' || substr(md5(random()::text || d.tenant_id || d.name), 1, 25),
    d.tenant_id,
    lower(replace(replace(replace(d.name, ' ', '-'), '/', '-'), '_', '-')),
    d.name,
    NULL,
    now(),
    now()
FROM (
    SELECT DISTINCT tenant_id, btrim(team) AS name
    FROM harness.agent_definitions
    WHERE tenant_id IS NOT NULL AND team IS NOT NULL AND btrim(team) <> ''
) d;

INSERT INTO harness.specialties (id, tenant_id, specialty_key, name, description, team_id, created_at, updated_at)
SELECT
    '0' || substr(md5(random()::text || d.tenant_id || d.name), 1, 25),
    d.tenant_id,
    lower(replace(replace(replace(d.name, ' ', '-'), '/', '-'), '_', '-')),
    d.name,
    NULL,
    NULL,
    now(),
    now()
FROM (
    SELECT DISTINCT tenant_id, btrim(specialty) AS name
    FROM harness.agent_definitions
    WHERE tenant_id IS NOT NULL AND specialty IS NOT NULL AND btrim(specialty) <> ''
) d;
