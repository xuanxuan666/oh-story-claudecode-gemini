# Oh Story repository guidance

## Scope

- Keep Claude Code, OpenCode, Codex, and OpenClaw behavior compatible unless a change explicitly targets one surface.
- Treat `skills/` as the canonical skill source. Do not hand-edit generated files under `.agents/skills/`, `skills/story-setup/references/opencode/agents/`, or `skills/story-setup/references/codex/agents/`.
- Do not modify `demo/` unless a task explicitly concerns examples or fixtures.

## Codex adapters

- `.agents/skills/*/SKILL.md` contains portable repository-discovery shims generated from `skills/*/SKILL.md`; regenerate them with `python scripts/generate-codex-skill-shims.py` after changing skill names or descriptions.
- `.codex-plugin/plugin.json` packages the canonical `skills/` directory for Codex plugin distribution. Keep its version aligned with `skills/story/VERSION` and `.claude-plugin/marketplace.json` metadata.
- Codex project agents are generated from `skills/story-setup/references/templates/agents/*.md` by `scripts/generate-codex-agents.py`.
- Codex project hooks live in `skills/story-setup/references/codex/hooks/` and must keep POSIX `command` and Windows `commandWindows` launchers behaviorally aligned.

## Editing rules

- Write cross-host instructions in platform-neutral terms. When a concrete host tool name is necessary, state the Codex equivalent or a direct-execution fallback.
- Resolve skill-relative references from the canonical `skills/<skill-name>/` directory, not from the `.agents/skills/` shim directory.
- Preserve UTF-8 for all Chinese text and scripts. On Windows, probe `python3`, `python`, then `py`; do not assume `python3` is a real interpreter.
- Keep generated adapters deterministic and avoid copying full skill resource trees into `.agents/skills/`.

## Verification

- Run `python scripts/generate-codex-skill-shims.py --check`.
- Run `bash scripts/check-codex-adapter.sh` and `bash scripts/test-codex-hooks.sh` from a POSIX shell or Git Bash.
- Run `bash scripts/static-check.sh` for structural changes to skills or references.
- Validate `.codex-plugin/plugin.json` with the Codex `plugin-creator` validator before release.
