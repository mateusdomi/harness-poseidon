-- ARC-09: definições canônicas built-in das personas de Architecture ("sob demanda"). Inserimos APENAS as
-- linhas-base (id estável, key, name, role, specialty, description) com tenant_id/owner IS NULL, do
-- mesmo modo que a migração inicial do catálogo (0015) e a de Delivery (0064) fizeram. O conteúdo
-- completo (persona, missão, princípios, ~14 campos) é preenchido de forma idempotente na
-- inicialização pelo BuiltInAgentDefinitionSeeder (guarda owner IS NULL). São definições de catálogo,
-- não agentes sempre-ligados: nenhuma linha em `agents` é criada aqui.
INSERT INTO harness.agent_definitions
    (id, agent_key, name, role, specialty, description)
VALUES
    ('01ARZ3NDEKTSV4RRFFQ69G5FB9', 'architecture-chief', 'Architecture Chief', 'specialist', 'Architecture orchestration and leadership',
     'Leads the architecture squad: frames the architectural question, routes it to the right specialist, and holds decisions to the quality bar.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBA', 'architecture-discovery', 'Discovery', 'specialist', 'Architecture discovery and context gathering',
     'Gathers the architectural context: current systems, constraints, drivers, and unknowns, before any decision is framed.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBB', 'architecture-solution-architect', 'Solution Architect', 'specialist', 'Solution architecture and option design',
     'Designs and compares solution options for a specific problem, framing each as a trade-off against the recorded drivers.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBC', 'architecture-enterprise', 'Enterprise Architecture', 'specialist', 'Enterprise architecture and alignment',
     'Aligns a solution with the enterprise landscape: standards, capabilities, and the target-state roadmap.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBD', 'architecture-integration', 'Integration Architect', 'specialist', 'Integration and interface architecture',
     'Designs how systems connect: contracts, protocols, boundaries, and the failure modes of every integration.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBE', 'architecture-data', 'Data Architect', 'specialist', 'Data architecture and modeling',
     'Designs the data: models, ownership, flows, consistency, and lifecycle across the systems.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBF', 'architecture-security', 'Security Architect', 'specialist', 'Security architecture and threat modeling',
     'Designs security into the architecture: trust boundaries, threat models, controls, and secure defaults.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBG', 'architecture-infrastructure', 'Infrastructure Architect', 'specialist', 'Infrastructure and platform architecture',
     'Designs the runtime foundation: compute, networking, deployment topology, scaling, and operability.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBH', 'architecture-rationalization-analyst', 'Rationalization Analyst', 'specialist', 'Portfolio and application rationalization',
     'Analyzes the application and technology portfolio for overlap, redundancy, and retirement candidates.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBJ', 'architecture-critic', 'Architecture Critic', 'specialist', 'Architecture review and critique',
     'Independently challenges architecture decisions against drivers, constraints, and evidence, and blocks weak ones.'),
    ('01ARZ3NDEKTSV4RRFFQ69G5FBK', 'architecture-adr-writer', 'ADR Writer', 'specialist', 'Architecture decision records',
     'Turns architecture decisions into clear, versioned ADRs with context, options, decision, and consequences.');
