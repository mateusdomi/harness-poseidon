-- Migration 0125: histórico append-only do perfil efetivo do projeto.
--
-- O perfil efetivo é o `constraint profile` que o gate da Fase 3 cobra. Ele precisa ser um
-- SNAPSHOT DE DECISÃO, não um cálculo refeito a cada leitura: quando o baseline global mudar em
-- dezembro, um card executado em agosto tem de continuar auditável sob a decisão que valia em
-- agosto. Por isso versão explícita e nada de UPDATE no conteúdo.
--
-- O corpo vai em JSON versionado porque o perfil evolui por área (novas modalidades, novos campos
-- de operação) e transformar cada campo em coluna faria de cada evolução uma migração. Só é
-- promovido a coluna o que precisa ser consultado sem desserializar.
CREATE TABLE project_effective_profiles
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version >= 1),
    fingerprint TEXT NOT NULL,
    baseline_version TEXT NOT NULL,
    modality TEXT NOT NULL,
    profile_json TEXT NOT NULL CHECK (json_valid(profile_json)),
    resolved_at TEXT NOT NULL,
    resolved_by TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('active', 'superseded')),
    PRIMARY KEY (tenant_id, project_id, version)
);

-- A vigente é a maior versão com status 'active'; o índice torna essa leitura barata no caminho
-- quente do despacho.
CREATE INDEX ix_project_effective_profiles_current
    ON project_effective_profiles (tenant_id, project_id, version DESC);
