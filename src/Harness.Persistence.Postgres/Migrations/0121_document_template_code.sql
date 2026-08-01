-- Migration 0121 (Fase 2A.1, correção de achado): o template que um documento realiza passa a ser
-- PROPRIEDADE DO DOCUMENTO, não do pedido que o criou.
--
-- A verificação de conformidade entrou apenas na criação, e o parecer independente encontrou o
-- caminho residual: cria-se o documento conforme e, na versão seguinte, grava-se qualquer coisa.
-- O gate valia para o primeiro instante da vida do documento e para mais nenhum — que é o mesmo
-- que não valer, porque o conteúdo que importa é o vigente, não o inicial.
--
-- Com a coluna, toda nova versão é verificada contra o mesmo template, sem depender de o cliente
-- reenviar o código (e sem permitir que ele o troque para escapar da verificação).
ALTER TABLE harness.documents ADD COLUMN template_code text NULL
    CHECK (template_code IS NULL OR length(template_code) BETWEEN 2 AND 4);
