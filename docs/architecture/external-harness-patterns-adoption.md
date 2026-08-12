# External Harness Patterns — Adoption Decision

**Status:** v1.0 — implementation freeze 2026-08-12  
**Scope:** Map useful ideas from five open-source harness/agent projects into Poseidon V3 without introducing new runtime dependencies, daemons or multi-agent orchestration.

## Decision rule

- Adopt natively only what fits the existing V3 stack (.NET, React/TypeScript, Playwright, SQLite/Postgres, `poseidon` CLI).
- Prefer concept-based clean implementation over copying code.
- If an idea requires a new service, database, language runtime, LLM reviewer or graph engine, mark it **NOT NOW** and document the extension point.

## Mapping

| Source | Idea | Poseidon Equivalent | Status | What was done |
|--------|------|---------------------|--------|---------------|
| **Fable Method** | Agent report != evidence; trap tests for false completion, summary-only validation, spec-vs-test mismatch, unauthorized outward action, N/A fraud, context pollution. | Validation Evidence Gate V3.2 + deterministic harness trap evals. | **ADOPTED** | Added adversarial trap tests in `tests/Harness.UnitTests/V3/V3HarnessTrapTests.cs` covering `FALSE_COMPLETE`, `SUMMARY_ONLY_VALIDATION`, `SPEC_VS_TEST`, `UNAUTHORIZED_OUTWARD_ACTION`, `N_A_FRAUD`, `CONTEXT_POLLUTION`. Fixtures are cheap and deterministic; no expensive LLM per run. |
| **Impeccable** | Deterministic visual/UI defect detectors (uncaught errors, failed network, broken images, invisible content, overflow, text occlusion, missing accessible names, broken mobile viewport). | Browser validation in Validation v3.2 + Playwright smoke tests. | **ALREADY HAD / PARTIAL ADOPT** | Reviewed existing 244-checklist and browser runs. Added native Playwright checks for uncaught script errors, failed network requests, broken images, severe overflow/text occlusion, interactive elements without accessible names and broken mobile viewport. Distinguishes **quality defect** from **design preference**; validation stops after a bounded polish loop. |
| **Better Harness** | Evidence maturity levels: Present → Wired → Exercised → OutcomeSupported (plus Missing/Unobserved/NotApplicable). | Capability/health read-models and validation manifest provenance. | **NOT NOW (documented)** | Concept mapped to future `ICapabilityHealth` read-model. Not restructuring persistence today; current `ValidationReport` + `handoffReadiness` already prove "configured != working". |
| **Code Review Graph** | Structural code graph for blast radius, affected flows, dependency context, minimal context, affected tests. | `ICodeIntelligence` extension point. | **NOT NOW (extension point)** | No graph engine, no Python/Tree-sitter service, no vector DB. Documented the future extension point where `ICodeIntelligence` could provide affected files, callers, imports and affected tests. Existing minimal-context selection via `V3KnowledgeSelector` is sufficient for the freeze. |
| **ECC** | Selective skills, harness security, doctor/status/repair philosophy. | `V3KnowledgeSelector`, secret scan, `poseidon doctor`. | **ALREADY HAD / PARTIAL ADOPT** | Verified `V3KnowledgeSelector` loads only stack-relevant knowledge. Extended deterministic security checks to inspect hardcoded secrets, insecure agent config, suspicious MCP config, excessive permissions, dangerous hooks, untrusted remote commands and provider-home leakage. Reused `poseidon doctor`; no new CLI. |

## What was NOT done

- No new dependencies on Fable, Impeccable, Better Harness, Code Review Graph or ECC.
- No Python daemon, no SQLite parallel instance, no vector database, no embeddings, no Tree-sitter service.
- No multi-agent council or LLM reviewer per microtask.
- No new scheduler, orchestrator, queue or graph engine.

## Extension points for post-freeze

1. `ICapabilityHealth` read-model can adopt Better Harness maturity levels when a safe migration is designed.
2. `ICodeIntelligence` can host a minimal structural graph provider (e.g., using Roslyn/TypeScript compiler APIs) without adding a new language runtime.
3. Additional Fable-style trap tests can be added as deterministic evals as new failure modes are observed.
