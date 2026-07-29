-- F8/D8: marcação de serviço VOLTADO AO USUÁRIO no manifesto de serviços do projeto.
--
-- O modo Negócio expõe apenas a tela que o cliente abre; o resto do ambiente (APIs,
-- workers, bancos) sobe junto como dependência e não aparece. Falso por padrão porque
-- marcar exige evidência no manifesto do projeto: sem evidência, o produto diz que não
-- sabe qual é a tela do cliente em vez de eleger uma ao acaso.
ALTER TABLE run_targets
    ADD COLUMN user_facing INTEGER NOT NULL DEFAULT 0 CHECK (user_facing IN (0, 1));
