-- Adiciona display_name aos vínculos de canal para armazenar identidade humanizada
-- (título/nome do chat do Telegram, nome do contato etc.) sem depender da
-- identidade técnica externa (ex.: chat id numérico).
ALTER TABLE channel_links ADD COLUMN IF NOT EXISTS display_name TEXT NULL CHECK (length(display_name) BETWEEN 1 AND 200);
