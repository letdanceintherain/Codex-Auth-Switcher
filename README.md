# Codex Auth Switcher

Codex Auth Switcher is a Windows desktop app for switching Codex authentication profiles without modifying the local thread library.

It is designed for people who move between ChatGPT login and API key workflows, but do not want profile switching to damage or rewrite local conversation state.

## What it changes

- `%USERPROFILE%\\.codex\\config.toml`
- `%USERPROFILE%\\.codex\\auth.json`

## What it does not touch

- `%USERPROFILE%\\.codex\\sessions`
- `%USERPROFILE%\\.codex\\archived_sessions`
- `%USERPROFILE%\\.codex\\session_index.jsonl`
- `%USERPROFILE%\\.codex\\state_5.sqlite`

## Why this exists

Switching Codex auth manually is easy to get wrong:

- ChatGPT login and API key mode can drift into different local states
- misconfigured API profiles can disable local response storage
- hand-editing `config.toml` and `auth.json` is tedious and fragile
- restarting the desktop app cleanly is easy to miss

This project turns that into a repeatable GUI workflow with backups and safer defaults.

## Features

- Manage ChatGPT snapshots and API profiles from a native Windows UI
- Capture the current ChatGPT login as a reusable snapshot
- Store API keys with Windows DPAPI under the current user account
- Auto-back up live `config.toml` and `auth.json` before each switch
- Restart Codex automatically after switching
- Support runtime Chinese and English UI switching
- Keep local response storage enabled for API profiles
- Provide a compatibility mode that tries to keep API usage aligned with the local Codex thread view

## Thread-view compatibility mode

The app includes a `Use local thread view compatibility` option for API profiles.

When enabled:

- the live config is written with `model_provider = "openai"`
- the third-party OpenAI-compatible endpoint is injected through `OPENAI_BASE_URL`
- the switcher avoids writing an invalid `[model_providers.openai]` override

This is the least invasive strategy we found for making API-backed sessions behave as closely as possible to the local Codex thread view, while still using a custom OpenAI-compatible backend.

## Safety model

The project does **not** rewrite the Codex thread database to force consistency.

Instead, it focuses on:

- stable auth switching
- preserving local response storage
- controlling runtime provider behavior
- minimizing changes to Codex-owned state

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK
- Inno Setup 6 to build the installer

## Quick start

1. Build or install the app.
2. Launch `CodexAuthSwitcher`.
3. Capture your current ChatGPT login if you want to preserve it as a named snapshot.
4. Create one or more API profiles in the GUI.
5. Switch profiles from the main window.

If the Codex desktop app is running, the switcher can close it, apply the profile, and restart it for you.

## Build from source

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1
```

This script:

1. restores the solution
2. runs tests
3. publishes a self-contained `win-x64` build
4. builds an installer with Inno Setup

Useful variants:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -NoInstaller
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -Clean
```

## Project layout

- `src/CodexAuthSwitcher.App` - WPF desktop app
- `src/CodexAuthSwitcher.Core` - profile switching, auth inspection, environment handling
- `tests/CodexAuthSwitcher.Core.Tests` - core regression tests
- `installer/CodexAuthSwitcher.iss` - Inno Setup installer definition
- `publish.ps1` - build and packaging entrypoint

## Current status

The project has working release builds and test coverage for the main profile-switching flows, including:

- ChatGPT snapshot capture
- API profile persistence
- local response storage protection
- openai thread-view compatibility mode
- runtime `OPENAI_BASE_URL` handling

## License

MIT. See [LICENSE](LICENSE).
