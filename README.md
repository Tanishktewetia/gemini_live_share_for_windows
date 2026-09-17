# Gemini Live Share

A Windows voice assistant that watches your screen and talks you through computer
tasks. Built for people who are not comfortable with computers, so it must never
invent what it sees and never act without confirmation.

C# · .NET 8 · WPF · Gemini Live API

## Start here

**[`PROJECT_STATE.md`](PROJECT_STATE.md) — current state, architecture, what works,
what's next.** Read that first, whether you are a person or an AI agent picking this
up. The other documents are plans and history.

| Doc | Purpose |
|---|---|
| [`PROJECT_STATE.md`](PROJECT_STATE.md) | The source of truth for what exists today |
| [`docs/PHASE7_ACCURACY_PLAN.md`](docs/PHASE7_ACCURACY_PLAN.md) | Current phase: accuracy and reliability |
| [`docs/PHASE6_BROWSER_AGENT.md`](docs/PHASE6_BROWSER_AGENT.md) | Browser agent spec (6a–6e done, 6f–6j pending) |
| [`PHASE_LOG.md`](PHASE_LOG.md) | Record of past fixes, with measurements |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Original build plan, Phases 0–5 (partly stale) |

## Build and run

```bash
dotnet build GeminiLiveShare.sln
```

Run the app from `src/GeminiLiveShare.App`. On first launch, enter a Gemini API key
in Settings; it is stored in the Windows Credential Vault.

Tests are a console harness, not xUnit:

```bash
dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj
```

Session logs, useful for any "why did it do that" question:
`%LOCALAPPDATA%\GeminiLiveShare\logs\session-yyyyMMdd.log`

## Browser extension setup (Phase 6)

Build the Native Messaging proxy:

```powershell
dotnet build .\src\GeminiLiveShare.NativeMessagingProxy\GeminiLiveShare.NativeMessagingProxy.csproj
```

Load `extension` as an unpacked extension at `chrome://extensions`, copy its ID into
`native-messaging/com.geminiliveshare.proxy.json` (replacing
`REPLACE_WITH_EXTENSION_ID`), and fix the manifest `path` if this repo is not at the
path shown there. Then register the host manifest:

```powershell
$hostManifest = 'C:\Users\tanis\Desktop\gemini_live\native-messaging\com.geminiliveshare.proxy.json'
$registryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.geminiliveshare.proxy'
New-Item -Path $registryPath -Force | Out-Null
Set-ItemProperty -Path $registryPath -Name '(Default)' -Value $hostManifest
```

For Edge, use `HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.geminiliveshare.proxy`.

With the app running, click the extension icon. The app's session log should show
that the extension connected.
