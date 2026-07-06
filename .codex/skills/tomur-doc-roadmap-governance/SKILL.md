---
name: tomur-doc-roadmap-governance
description: Maintain Tomur documentation governance across AGENTS.md, README.md, README.en.md, ROADMAP.md, CHANGELOG.md, and docs evidence files. Use when adding roadmap capabilities, moving items between planned/current/completed, documenting APIs/CLI/runtime/Web work, updating release or smoke evidence, renaming public commands or paths, or checking that Tomur docs stay consistent with implementation and product boundaries.
---

# Tomur Doc Roadmap Governance

## Core Rule

Read `AGENTS.md` first. Tomur documentation is authoritative product governance, not casual notes. Keep the tone restrained, clear, and professional. Default to Chinese unless a file is already explicitly English, such as `README.en.md` or some historical docs.

## Document Roles

- `AGENTS.md`: long-term constraints for agents and automation. Update only when product/engineering rules change.
- `README.md`: public project homepage in Chinese. Keep it concise: positioning, goals, core capabilities, architecture, local state, runtime assets, roadmap entry.
- `README.en.md`: English mirror of public README content when the Chinese README changes materially.
- `ROADMAP.md`: only long-term/current/future phase plan, acceptance criteria, and remaining work.
- `CHANGELOG.md`: completed history only. Move completed phase detail here instead of expanding ROADMAP.
- `docs/*`: evidence, audits, smoke logs, release package details, and maintenance matrices.

Do not create or reference `MIGRATION_ROADMAP.md`.

## Product Boundaries

Keep every doc aligned with these constraints:

1. Tomur is a local AI runtime and developer workbench built with .NET 10 and C#.
2. Tomur is local-first, privacy-sensitive, low-ops, and single-process.
3. Tomur is not an industry solution platform, digital employee platform, Mission Control platform, complex workflow governance platform, multi-tenant server, or admin shell.
4. Do not mention `Camel.NET`, `CamelNET`, migration origin, old server modules, or old platform background in Tomur docs.
5. Do not describe planned or unverified capabilities as implemented.
6. Do not introduce PostgreSQL, RBAC, SSO, complex audit, multi-tenant governance, or enterprise deployment as default capabilities.
7. Keep the Web UI Chat-first; Models, Downloads, Runtime, and Files stay in Settings/status/contextual diagnostics unless the roadmap explicitly changes.

## Status Language

Use precise status language:

- Completed: code exists and the documented behavior is implemented.
- Smoke-validated: real command/API/model evidence exists and is recorded.
- Diagnosed: unavailable paths return clear structured diagnostics.
- Backend visible: Tomur can see backend libraries or configuration.
- Device enumerable: Tomur can enumerate hardware.
- Real inference passed: an actual model run succeeded and evidence exists.
- Not run or pending: no execution evidence yet.

Never collapse these into a vague "supported" if the distinction matters.

## Update Workflow

1. Inspect `git status --short` before editing; preserve unrelated user changes.
2. Read the implementation or evidence files before changing claims.
3. If adding a new roadmap capability, first place it in the appropriate ROADMAP phase before implementing or documenting it as a current target.
4. If a public command, API path, data directory, service name, unit name, or default behavior changes, update README, ROADMAP, and AGENTS.md together.
5. If work is completed, update CHANGELOG with concrete completed history.
6. If work remains, update ROADMAP with remaining scope and acceptance criteria.
7. If docs mention validation, ensure the evidence exists or mark it clearly as not run/pending.

## Tone And Shape

Avoid oral, internal memo, and marketing phrasing. Prefer direct statements such as:

- "Tomur provides..."
- "The current boundary is..."
- "Still pending..."
- "This smoke record covers..."

Avoid phrases like "核心程序名就是", "目标体验接近", "当前状态很早期", and "先怎样再怎样".

Small emoji markers are acceptable in README/ROADMAP when they help navigation, but do not make the copy playful or promotional.
