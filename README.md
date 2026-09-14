# Codex Auth Switcher

English | [简体中文](#简体中文)

A Windows app for switching ChatGPT accounts and API profiles while continuing the same local Codex conversations.

## Download

- [Latest release and installer](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases/latest)
- [Source repository](https://github.com/letdanceintherain/Codex-Auth-Switcher)

Use the installer for normal use, or run the portable executable directly. Windows x64; the release includes the .NET runtime.

## What changed in v1.2.0

The provider name you enter is now the actual Codex provider ID. The old compatibility flag no longer silently forces it to `openai`.

Codex filters its default thread list by provider and can restore the provider saved with a conversation. Keeping files untouched was insufficient: conversations saved under a different provider could disappear from the list or resume with an old route. On each switch, the app now backs up and synchronizes local conversation routing metadata with the selected provider.

The operation preserves message content, thread IDs, titles, timestamps, pinned/section placement, and archive state. It covers active and archived JSONL rollouts, SQLite thread provider IDs, persisted thread settings, and history-cache byte offsets.

## Usage

1. For a ChatGPT account, sign in normally in Codex, then open the switcher or click Refresh. Save/capture the account before logging into another one.
2. For an API profile, enter its name, provider ID (for example `crs` or `my-provider`), base URL, model, and API key.
3. Finish running Codex tasks. Select a profile and click Switch. You can let the app close and restart Codex Desktop; separately running Codex CLI tasks must also be closed.
4. Open an existing conversation and continue. Its provider follows the selected account/API automatically.

For a custom provider the config uses `model_provider = "my-provider"` and `[model_providers.my-provider]`. If you explicitly enter `openai`, the built-in provider and `openai_base_url` are used. ChatGPT snapshots return to the official built-in OpenAI route.

Old profiles remain usable. There is no need to recreate them: provider identity is recalculated using the saved name, endpoint, and key. The misleading local-thread-view checkbox has been removed; continuity runs automatically on every switch.

## Data handling and backups

The app resolves `CODEX_HOME` (default: `%USERPROFILE%/.codex`) and the configured `sqlite_home` / `CODEX_SQLITE_HOME`.

It updates:

- Authentication and managed model-routing settings in `config.toml` and `auth.json`.
- Provider fields in local `sessions` and `archived_sessions` JSONL files, including persisted thread settings.
- `threads.model_provider` in `state_5.sqlite`.
- Read offsets in `thread_history_1.sqlite` when edited JSONL metadata changes byte lengths.

Message records are preserved byte-for-byte. History-cache messages/turns, `session_index.jsonl`, and desktop global/sidebar state are not rewritten. TOML changes preserve unrelated settings, multiline instructions, comments, and custom provider headers.

Before applying changes, the app saves backups under `CODEX_HOME/auth-switcher/backups/<timestamp>`. SQLite backups include uncheckpointed WAL data. Failed writes roll back the affected files/databases. A `continuity-journal.json` records backup-to-original mappings and completion status. If the process or machine stops mid-switch, an unfinished journal blocks further switches until the listed backups are restored with Codex closed. Do not delete an unfinished journal to bypass recovery.

ChatGPT snapshots and API keys stored in profiles use Windows DPAPI for the current user. Recovery backups contain credentials and conversation data; keep them private. Large conversation libraries require time and disk space for backups and staging.

## Supported configuration and limits

- Windows 10/11 x64; file-based Codex authentication (`cli_auth_credentials_store = "file"` or the current file default).
- OpenAI-compatible Responses endpoints. Custom profiles use the saved API key via `auth.json`.
- Current `state_5.sqlite` and JSONL rollouts. Unrecognized database versions, compressed rollouts, linked rollout paths, invalid files, and locked databases produce an error instead of a partial switch.
- All local conversations in the selected Codex home follow the selected provider. This is not per-thread provider isolation.
- Config-profile or managed-policy overrides must agree with the selected route. Cloud-only chats and remote-host state are outside this local switcher's scope.
- The selected backend must support the conversation's model/tools. Expired account credentials may require normal sign-in. Continuity does not grant models or capabilities unavailable on the selected backend.

## Build and verify

Requirements: .NET 8 SDK; Inno Setup 6 for the installer.

```powershell
dotnet test .\CodexAuthSwitcher.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -Configuration Release
```

Outputs: `artifacts/publish/win-x64/CodexAuthSwitcher.exe` and `artifacts/installer/CodexAuthSwitcher-Setup-1.2.0.exe`.

The regression suite uses real SQLite databases and JSONL histories to test provider round-trips, message preservation, archive/sidebar metadata, WAL backups, rollback, quoted provider names, and Windows canonical paths.

An optional smoke check reproduces provider filtering and then lists/resumes the same conversation using a real Codex app server. It uses generated conversations, fake credentials, loopback endpoints, and a temporary Codex home; no model turn is submitted.

```powershell
$env:SWITCHER_SMOKE_CLI = 'C:\path\to\codex.exe'
python .\tools\ContinuitySmoke\verify.py
```

The smoke test prints its temporary directory for inspection. Tested with CLI 0.118.0 and the Desktop backend 0.154.0-alpha.6.2.

[Changelog](CHANGELOG.md) · [MIT License](LICENSE)

---

## 简体中文

Codex Auth Switcher 是 Windows 上的 Codex 账号/API 切换工具，核心功能是切换后继续使用同一批本地对话。

### 下载

[下载最新版安装包或便携版](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases/latest)。

### v1.2.0 修复了什么

- 填写的模型商名称会直接生效，不再被旧版兼容选项强制改成 `openai`。
- 切换时自动同步已有对话保存的模型商标记，解决切换后历史列表缺失、继续对话仍使用旧模型商的问题。
- 支持已有和归档的本地对话，保留正文、对话 ID、标题、时间、置顶、分组与归档状态。
- 同步前备份；写入失败时回滚。处理了 Windows 特殊路径、聊天缓存读取位置和旧版配置迁移。
- 模型商名称支持中文、空格、点号；保留其他 Codex 配置和多行指令。

### 使用方法

1. ChatGPT 账号：先在 Codex 正常登录，再打开切换器或点击刷新保存账号。登录其他账号前，先保存当前账号。
2. API：填写配置名、模型商名称（例如 `crs`）、API 地址、模型和密钥。
3. 结束正在执行的任务，选择配置并切换。可以自动关闭并重启桌面端；单独运行的 Codex CLI 也需要退出。
4. 打开原有对话继续聊天，无须手动统一模型商名称。

旧配置无需重建。升级后选择原来的配置切换一次，即会使用保存的模型商名称并同步历史对话。切回 ChatGPT 时恢复官方 OpenAI 路由。

### 数据与备份

程序使用同一个 `CODEX_HOME`，并遵守 `sqlite_home` 配置。每次切换会备份认证、配置、将被修改的 JSONL 文件和 SQLite 数据库，再同步模型商元数据及相关读取位置；不会删除聊天或改写消息正文。

备份位于 `CODEX_HOME/auth-switcher/backups/<时间戳>`。备份包含聊天和认证信息，请妥善保管。历史较多时，备份和同步会占用一定时间及磁盘空间。

如果电脑或程序在切换途中退出，程序会检测未完成的 `continuity-journal.json` 并阻止后续切换。请在关闭 Codex 后按该文件中的映射恢复备份；不要直接删除日志绕过恢复。

当前支持 Windows x64、file 认证存储、Responses 接口、`state_5.sqlite` 和 JSONL 历史。未知数据库版本、压缩历史、路径链接、文件损坏或锁定会阻止切换。选中目录内的所有本地对话会跟随当前模型商，云端或远程主机对话不在同步范围内。

模型商本身仍需要支持对话使用的模型和工具；过期账号可能需要重新登录。构建与隔离测试方法见上方英文说明。
