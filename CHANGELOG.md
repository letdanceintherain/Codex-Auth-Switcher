# Changelog

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
