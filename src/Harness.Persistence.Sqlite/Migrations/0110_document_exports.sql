-- EXPORTAÇÃO DE DOCUMENTOS — cada pacote entregue é um fato auditável.
--
-- Quando o dono baixa o ZIP, conteúdo interno vira externo: vai para um sócio, um cliente, uma
-- auditoria. Meses depois a pergunta que aparece é "qual versão do documento foi parar na mão de
-- quem?", e ela precisa ter resposta sem depender de memória — por isso registramos quem pediu, o
-- que saiu (o manifesto, com título, versão e hash de cada documento) e quando.
--
-- A tabela é APPEND-ONLY, como o ledger de auditoria: uma exportação não é editada nem apagada.
-- Exportar de novo gera OUTRA linha, porque é outro fato — o pacote anterior continua tendo
-- existido, mesmo que o conteúdo do projeto tenha mudado depois.
--
-- Faixa 0110 reivindicada no quadro de coordenação: a faixa 0088-0089 pré-alocada à F5 já havia
-- sido ocupada pelo dono e pela F6.
CREATE TABLE IF NOT EXISTS document_exports
(
    tenant_id TEXT NOT NULL,
    export_id TEXT NOT NULL CHECK (length(export_id) = 26),
    project_id TEXT NOT NULL,
    document_count INTEGER NOT NULL CHECK (document_count >= 0),
    requested_by_profile_id TEXT NOT NULL,
    manifest_json TEXT NOT NULL
        CHECK (json_valid(manifest_json) AND json_type(manifest_json) = 'object'),
    created_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, export_id)
);

CREATE INDEX IF NOT EXISTS ix_document_exports_project
    ON document_exports (tenant_id, project_id, created_at);
