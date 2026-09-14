# Codex Auth Switcher

English | [简体中文](#简体中文)

A Windows app for switching ChatGPT accounts and API profiles while continuing the same local Codex conversations.

## Download

- [Latest release and installer](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases/latest)
- [Source repository](https://github.com/letdanceintherain/Codex-Auth-Switcher)

Use the installer for normal use, or run the portable executable directly. Windows x64; the release includes the .NET runtime.

## What changed in v1.2.2

The switcher now follows SQLite's selected history path instead of scanning every active copy. A reverted thread can have many files sharing the same logical ID; those are not necessarily duplicates and are no longer rejected just because their first metadata IDs match.

Paginated histories use a new immutable rollout generation on a provider change. The logical conversation ID remains the same, while the original file, its cache and any inherited-history references remain intact. Codex materializes the new generation's cache when needed. Unindexed copies and archives are not deleted or rewritten.

Archived conversations are now skipped. Duplicate IDs, invalid JSONL, or compressed files inside `archived_sessions` no longer block an ordinary switch. Archive files, archived thread rows, and their cache offsets remain unchanged. To continue an archived conversation on a new provider later, unarchive it in Codex and switch again.

The provider name you enter is now the actual Codex provider ID. The old compatibility flag no longer silently forces it to `openai`.

Codex filters its default thread list by provider and can restore the provider saved with a conversation. Keeping files untouched was insufficient: conversations saved under a different provider could disappear from the list or resume with an old route. On each switch, the app now backs up and synchronizes local conversation routing metadata with the selected provider.

The operation preserves message content, logical thread IDs, titles, timestamps, and pinned/section placement. It covers indexed unarchived histories and their routing. Archives are excluded.

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
- `threads.model_provider` and, for paginated histories, the selected `rollout_path` in `state_5.sqlite`.
- Provider fields in new paginated rollout copies; originals and old physical caches remain untouched.
- Provider fields in indexed legacy JSONL files, with matching physical-rollout projection and turn-boundary offsets adjusted together.

Message records are preserved byte-for-byte. History-cache messages/turns, `session_index.jsonl`, and desktop global/sidebar state are not rewritten. TOML changes preserve unrelated settings, multiline instructions, comments, and custom provider headers.

Before applying changes, the app saves backups under `CODEX_HOME/auth-switcher/backups/<timestamp>`. SQLite backups include uncheckpointed WAL data. Paginated originals remain in place as immutable recovery sources. Failed writes roll back the affected files/databases and remove only newly created replacement rollouts. A `continuity-journal.json` records target/backup mappings and completion status (`existed: false` identifies a newly created target). If the process or machine stops mid-switch, an unfinished journal blocks further switches until recovery with Codex closed. Do not delete an unfinished journal to bypass recovery.

ChatGPT snapshots and API keys stored in profiles use Windows DPAPI for the current user. Recovery backups contain credentials and conversation data; keep them private. Each paginated provider change may retain a full local rollout copy plus its subsequently rebuilt cache. Large libraries require time and disk space. Do not manually remove old rollouts: another conversation or archive may reference them.

## Supported configuration and limits

- Windows 10/11 x64; file-based Codex authentication (`cli_auth_credentials_store = "file"` or the current file default).
- OpenAI-compatible Responses endpoints. Custom profiles use the saved API key via `auth.json`.
- Current `state_5.sqlite` and JSONL rollouts. Unrecognized database versions, compressed rollouts, linked rollout paths, invalid files, and locked databases produce an error instead of a partial switch.
- Unarchived local conversations in the selected Codex home follow the selected provider. Archived conversations keep their original route. This is not per-thread provider isolation.
- With a state DB, unindexed files are left alone. Without a DB, only unambiguous legacy histories are supported; a history cache without its state index or a paginated file without an index is rejected.
- Config-profile or managed-policy overrides must agree with the selected route. Cloud-only chats and remote-host state are outside this local switcher's scope.
- The selected backend must support the conversation's model/tools. Expired account credentials may require normal sign-in. Continuity does not grant models or capabilities unavailable on the selected backend.

## Build and verify

Requirements: .NET 8 SDK; Inno Setup 6 for the installer.

```powershell
dotnet test .\CodexAuthSwitcher.sln -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish.ps1 -Configuration Release
```

Outputs: `artifacts/publish/win-x64/CodexAuthSwitcher.exe` and `artifacts/installer/CodexAuthSwitcher-Setup-1.2.2.exe`.

The regression suite uses real SQLite databases and JSONL histories to test provider round-trips, message preservation, archive/sidebar metadata, WAL backups, rollback, quoted provider names, and Windows canonical paths.

An optional smoke check reproduces provider filtering, creates two real forks, and reads/resumes all three conversations across providers using a real Codex app server. It uses generated conversations, fake credentials, loopback endpoints, and a temporary Codex home. The optional context check submits a synthetic turn only to a local mock, verifying the inherited message reaches the model request; no real provider is contacted.

```powershell
$env:SWITCHER_SMOKE_CLI = 'C:\path\to\codex.exe'
python .\tools\ContinuitySmoke\verify.py
# For a current desktop backend, also test paginated histories and inherited model context:
$env:SWITCHER_SMOKE_PAGINATED = '1'
$env:SWITCHER_SMOKE_CONTEXT = '1'
python .\tools\ContinuitySmoke\verify.py
```

The smoke test prints its temporary directory for inspection. Tested with CLI 0.118.0 and the Desktop backend 0.154.0-alpha.6.2.

[Changelog](CHANGELOG.md) · [MIT License](LICENSE)

---

## 简体中文

Codex Auth Switcher 是 Windows 上的 Codex 账号/API 切换工具，核心功能是切换后继续使用同一批本地对话。

### 下载

[下载最新版安装包或便携版](https://github.com/letdanceintherain/Codex-Auth-Switcher/releases/latest)。

### v1.2.2 修复了什么

- 按数据库记录的当前历史路径同步，不再把回退/分叉产生的旧文件误判为重复会话。
- 分页历史使用新副本承接模型商变更，保留原文件、旧缓存及继承关系，对话 ID 不变。
- 区分对话 ID 与实际历史文件 ID，旧格式历史的缓存游标与对话轮次边界一并校正。
- 默认跳过归档目录，解决多个归档文件共用会话 ID 导致切换失败的问题。归档文件、数据库记录和缓存位置保持不变，不删除或合并历史。
- 填写的模型商名称会直接生效，不再被旧版兼容选项强制改成 `openai`。
- 切换时自动同步已有对话保存的模型商标记，解决切换后历史列表缺失、继续对话仍使用旧模型商的问题。
- 同步未归档的本地对话，保留正文、对话 ID、标题、时间、置顶和分组；不再同步归档对话。
- 同步前备份；写入失败时回滚。处理了 Windows 特殊路径、聊天缓存读取位置和旧版配置迁移。
- 模型商名称支持中文、空格、点号；保留其他 Codex 配置和多行指令。

### 使用方法

1. ChatGPT 账号：先在 Codex 正常登录，再打开切换器或点击刷新保存账号。登录其他账号前，先保存当前账号。
2. API：填写配置名、模型商名称（例如 `crs`）、API 地址、模型和密钥。
3. 结束正在执行的任务，选择配置并切换。可以自动关闭并重启桌面端；单独运行的 Codex CLI 也需要退出。
4. 打开原有对话继续聊天，无须手动统一模型商名称。

旧配置无需重建。升级后选择原来的配置切换一次，即会使用保存的模型商名称并同步历史对话。切回 ChatGPT 时恢复官方 OpenAI 路由。

### 数据与备份

程序使用同一个 `CODEX_HOME`，并遵守 `sqlite_home` 配置。每次切换会备份认证、配置和 SQLite 数据库；旧格式文件先备份再同步，分页历史则生成新副本并更新索引，原文件和原缓存保留。不会删除聊天或改写消息正文。

备份位于 `CODEX_HOME/auth-switcher/backups/<时间戳>`。备份包含聊天和认证信息，请妥善保管。分页历史切换模型商可能保留完整文件副本及新缓存，会增加磁盘占用；旧文件可能仍被其他对话或归档引用，不要自行清理。

如果电脑或程序在切换途中退出，程序会检测未完成的 `continuity-journal.json` 并阻止后续切换。请在关闭 Codex 后按该文件中的映射恢复备份；不要直接删除日志绕过恢复。

当前支持 Windows x64、file 认证存储、Responses 接口、`state_5.sqlite` 和 JSONL 历史。未知数据库版本、未归档的压缩历史、路径链接、文件损坏或锁定会阻止切换。未归档的本地对话会跟随当前模型商；归档、云端或远程主机对话不在同步范围内。以后需要继续归档对话时，先在 Codex 中取消归档，再切换一次即可同步。

模型商本身仍需要支持对话使用的模型和工具；过期账号可能需要重新登录。构建与隔离测试方法见上方英文说明。
