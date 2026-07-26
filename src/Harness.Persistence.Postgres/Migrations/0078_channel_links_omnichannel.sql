-- Amplia o conjunto fechado de canais com os adapters WhatsApp e e-mail e registra
-- a última entrada recebida por vínculo (last_inbound_at), insumo durável do
-- roteamento de saída para o último canal ativo da conversa.
ALTER TABLE harness.channel_links
    DROP CONSTRAINT channel_links_kind_check;

ALTER TABLE harness.channel_links
    ADD CONSTRAINT channel_links_kind_check
        CHECK (kind IN ('terminal', 'telegram', 'teams', 'whatsapp', 'email'));

ALTER TABLE harness.channel_links
    ADD COLUMN last_inbound_at timestamptz NULL;
