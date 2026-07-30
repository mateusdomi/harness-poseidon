-- ESCOPO DE PATH DA PERSONA (B8/F17) — sem estes dois campos o lint de definição não tem o que
-- julgar: ele decide sobre CAMINHOS que a persona pode e não pode tocar, e a definição só declarava
-- tecnologia (stacks) e limitações em texto livre. Julgar um pelo outro recusa persona legítima,
-- que é pior do que não ter guarda nenhuma.
--
-- Uma persona é uma procuração. Estes campos são o que ela autoriza — e, principalmente, o que ela
-- recusa: a ausência de denylist é a omissão que deixa governança, segredo e o próprio quadro
-- abertos por esquecimento. Omissão não é decisão.
ALTER TABLE agent_definitions ADD COLUMN allowed_scopes_json TEXT NOT NULL DEFAULT '[]'
    CHECK (json_valid(allowed_scopes_json) AND json_type(allowed_scopes_json) = 'array');
ALTER TABLE agent_definitions ADD COLUMN denied_scopes_json TEXT NOT NULL DEFAULT '[]'
    CHECK (json_valid(denied_scopes_json) AND json_type(denied_scopes_json) = 'array');
