-- F8/D8: marcação de serviço VOLTADO AO USUÁRIO no manifesto de serviços do projeto.
--
-- O modo Negócio expõe apenas a tela que o cliente abre; o resto do ambiente (APIs,
-- workers, bancos) sobe junto como dependência e não aparece. Falso por padrão porque
-- marcar exige evidência no manifesto do projeto: sem evidência, o produto diz que não
-- sabe qual é a tela do cliente em vez de eleger uma ao acaso.
--
-- Faixa 0111 reivindicada no quadro de coordenação: a F8 não tem faixa pré-alocada na
-- arquitetura, e as faixas 0101-0109 pertencem às fases F14, F15, F16 e F17.
ALTER TABLE run_targets
    ADD COLUMN user_facing BOOLEAN NOT NULL DEFAULT FALSE;
