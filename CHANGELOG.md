# Changelog

## 1.2.0 - 2026-09-14

- Honor the entered API provider ID even for profiles saved with the old compatibility flag.
- Synchronize local thread provider metadata on every switch so existing and archived conversations remain visible and resumable across providers.
- Update JSONL session metadata, persisted thread settings, SQLite thread provider IDs, and history projection byte offsets together; preserve messages, IDs, timestamps, sidebar placement, and archive state.
- Back up changed JSONL files and SQLite databases (including WAL data), roll back failed writes, and record a recovery journal for interrupted switches.
- Restore ChatGPT accounts to the built-in OpenAI route even when captured with stale API settings.
- Parse TOML syntax to support quoted/Unicode provider names and preserve multiline instructions, comments, and provider-specific headers.
- Recognize legacy API fingerprints and Windows verbatim rollout paths.
- Recheck running Codex processes before applying changes and remove the misleading provider-forcing checkbox.
- Add realistic SQLite/JSONL regressions and an isolated smoke check using the real Codex app server.

## 1.1.0 - 2026-07-29

- Preserve the local thread library by restoring only authentication and managed model-routing keys.
- Encrypt ChatGPT auth snapshots with Windows DPAPI and migrate v1.0 plaintext snapshots automatically.
- Refresh a matched ChatGPT profile from the latest live OAuth state so saved accounts do not retain stale tokens.
- Use the supported `openai_base_url` setting instead of a persistent `OPENAI_BASE_URL` environment override.
- Detect the current `ChatGPT.exe` desktop process as well as legacy `Codex.exe` builds.
- Verify that Codex actually restarts before reporting success.
- Write `config.toml` and `auth.json` transactionally with rollback on failure.
- Respect `CODEX_HOME` and reject credential-store modes that cannot be switched through `auth.json` safely.
- Add regression coverage for thread/state preservation, legacy migration, rollback, and current process paths.
- Add a Windows GitHub Actions build and repair clean-checkout publishing.

## 1.0.0 - 2026-03-29

- Initial public release.
