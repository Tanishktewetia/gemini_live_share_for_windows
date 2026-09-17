# PROJECT STATE — Gemini Live Share (Windows)

**Read this file first. It is the single source of truth for what exists today.**
Last updated: 2026-09-18 · Branch: `main` · Latest commit: `d2550ae`

Everything below reflects the code as it is now. The other docs are either plans
(what we intended) or history (what we fixed). Read them only when this file
points you to them.

| Doc | What it is | Read it when |
|---|---|---|
| `PROJECT_STATE.md` (this file) | Current state, architecture, what's done, what's next | Always, first |
| `docs/PHASE6_BROWSER_AGENT.md` | Full spec for the browser agent (6a–6j) | Working on browser tools |
| `docs/PHASE7_ACCURACY_PLAN.md` | Plan for the current accuracy/reliability work | Working on 7a–7g |
| `PHASE_LOG.md` | Deep record of past fixes, with measurements | Debugging a regression in an area it covers |
| `docs/ARCHITECTURE.md` | Original phased build plan (Phases 0–5), partly stale | Historical context only |
| `gemini-live-share-architecture.md` | Superseded older copy of the above | Never |

---

## 1. What this product is

A Windows desktop voice assistant that watches the user's screen and guides them
through computer tasks by talking to them. **The target user is someone who is not
comfortable with computers**, for example an older person who needs to be told
where to click.

That audience sets the hard requirements:

- It must **never invent** what it sees or what it did. A confident wrong answer is
  worse than "I can't see that clearly."
- It must **point at things** rather than give abstract instructions.
- It must hear clearly and speak smoothly on ordinary home internet.
- It must never act on anything consequential (submitting a form, clicking a
  button) without explicit confirmation.

## 2. Tech stack

- **C# / .NET 8 / WPF**, Windows 11
- **Gemini Live API** over WebSocket: `models/gemini-3.1-flash-live-preview`
- **Windows.Graphics.Capture** for the screen, **SkiaSharp** for image encoding
- **NAudio** plus the Windows Voice Capture DSP for microphone and echo cancellation
- **SQLite** for chat history, **Windows Credential Vault** for the API key
- **UI Automation** and **Windows.Media.Ocr** for credential detection
- **Chrome MV3 extension** plus a Native Messaging proxy for browser control

## 3. Solution layout

```
GeminiLiveShare.sln
├── src/GeminiLiveShare.App/              WPF UI (MVVM)
│   ├── Views/         MainWindow (sidebar + chat), OverlayWindow ("dynamic island"),
│   │                  BrowserAgentDebugPanel, settings/rename/close dialogs
│   ├── ViewModels/    MainViewModel (session orchestration + titles), ChatHistoryViewModels,
│   │                  SettingsViewModel
│   └── Tray/          TrayIconManager (background running)
├── src/GeminiLiveShare.Core/             All logic, no UI
│   ├── Gemini/        GeminiLiveClient (WebSocket + setup + system instruction),
│   │                  SessionOrchestrator (the hub: audio ⇄ video ⇄ API ⇄ storage),
│   │                  GeminiTitleGenerationService, ReconnectPolicy, Models/
│   ├── Audio/         AudioCaptureService, EchoCancellingMicrophone (Voice Capture DSP),
│   │                  AudioPlaybackService, PcmPlaybackQueue (lossless)
│   ├── Vision/        ScreenCaptureService (1 fps), ImageProcessingService (resize/encode/
│   │                  change-detect), CredentialBlurService (UI Automation),
│   │                  OcrCredentialDetector, CredentialMatcher
│   ├── BrowserAgent/  BrowserAgentBridge (Named Pipe server), BrowserAgentToolRegistry, Models/
│   ├── Storage/       ChatHistoryRepository (SQLite)
│   ├── Security/      ApiKeyVaultService, SensitiveContentFilterSettings
│   ├── Diagnostics/   SessionDiagnostics (file log)
│   └── Interop/       GlobalHotkey (Ctrl+Y), overlay settings
├── src/GeminiLiveShare.NativeMessagingProxy/   stdio ⇄ Named Pipe relay (dumb proxy)
├── src/GeminiLiveShare.Tests/Program.cs        Custom test harness (NOT xUnit) — see §7
├── extension/                            MV3 extension (service worker + field scanner)
├── native-messaging/                     Chrome host manifest
└── tools/sample-forms/                   Test HTML forms for browser-agent work
```

### How a conversation flows

```
Microphone ─► EchoCancellingMicrophone ─► 40ms PCM chunks ─┐
                                                           ├─► SessionOrchestrator ─► GeminiLiveClient
Screen (1 fps) ─► ImageProcessingService ─► JPEG base64 ───┘        │                  (one WebSocket,
   (credential blur, change detection, adaptive quality)            │                   one send lock)
                                                                    ▼
                          AudioPlaybackService ◄── PCM audio ── Gemini replies ──► transcripts ──► SQLite
```

**Key point about the send lock:** frames and microphone audio share one WebSocket.
A large frame blocks speech. This caused real, measured voice damage before, so
never increase frame size or rate without re-measuring the microphone loss in the
session log. See `PHASE_LOG.md` §1 for the numbers.

## 4. What works today (verified)

| Area | State |
|---|---|
| Voice conversation, barge-in | Working. Lossless playback queue; long replies no longer scramble |
| Echo cancellation (speakers, no headset) | Working via Windows Voice Capture DSP, with raw-capture fallback |
| Screen sharing, 1 fps | Working. Native resolution up to 2560 px, JPEG q90, change detection, adaptive quality |
| Credential protection | Working. UI Automation plus OCR, both must pass or the frame is dropped |
| Chat history, titles, rename | Working. SQLite, AI-generated titles, backfill at startup |
| Overlay UI, tray, global hotkey | Working (Phase 5 complete) |
| Reconnect with session resumption | Working |
| Diagnostics log | Working: `%LOCALAPPDATA%\GeminiLiveShare\logs\session-yyyyMMdd.log` |
| Browser agent 6a–6e | Working: extension ⇄ proxy ⇄ app pipe, `get_active_page`, `get_form_fields`, page context injected into the conversation. **Read-only** |
| Google Search in Live | **Requested but refused.** See §5 |

## 5. Known problems (the current work)

These were diagnosed on 2026-09-17/18 with real sessions and the diagnostics log.

### P1 — Gemini invents details it cannot see (highest priority)
Asked to count taskbar icons it named Edge, Teams and VS Code, none of which were
on screen. Asked where the mouse pointer was, it guessed. Asked to count desktop
icons it said "42" and gave a made-up position for an icon.

**Cause: not the capture pipeline.** The log shows frames delivered well: 160 sent,
0 dropped, ~228 KB each at quality 90, 2 ms uploads. The Live API compresses every
video frame to a small fixed size, so 24 px icons and small text become unreadable.
The model then fills the gap with a plausible guess. This is why the same task works
on a phone (large UI) and fails on a desktop (dense UI).

**Fix direction:** stop using vision for things Windows can answer exactly. Use UI
Automation for elements, the taskbar and desktop icons; add a zoom tool that crops
from the full-resolution capture for the rest; and instruct the model never to guess.
Details in `docs/PHASE7_ACCURACY_PLAN.md`.

### P2 — No web search
**The code is correct**: `tools: [{ googleSearch: {} }]` is sent in the setup message
(`Models/SetupMessage.cs`, `GeminiLiveClient.cs:310`). Google **rejects** it in about
400 ms with a quota error, because the project is on the **free tier**. The app then
reconnects without search and tells the model to say it cannot search.

Confirmed in the log on two separate sessions, and in the AI Studio console
(free-tier badge, plus 409/429 errors on Sep 17).

**Fix:** enable billing on the Google Cloud project behind the API key, then retest.
No code change may be needed. **Restart the app when testing** — "search unavailable"
is cached for the whole app run (`GeminiLiveClient.cs:24`).

### P3 — No way to show the user where to click
The assistant can say "click Next" but cannot point at it. For the target audience
this is the difference between usable and useless. Planned as 7g.

### P4 — A fresh session loses all context
When a session ends and a new one starts, Gemini greets the user again and forgets
everything, including that screen sharing was on. The Live API also ends long
sessions by design. (Note: the mid-conversation greeting seen on Sep 17 was a
deliberate new conversation, not a bug, but the underlying gap is real.)

## 6. What's next

**Current phase: Phase 7 — Accuracy and reliability.** Full detail, with "done when"
criteria for each step, is in `docs/PHASE7_ACCURACY_PLAN.md`.

| Order | Step | Size |
|---|---|---|
| 1 | 7c System instruction: never guess; ask the user to point | S |
| 2 | 7d UI Automation tools: element under cursor, taskbar, desktop icons, focused window | M |
| 3 | 7f Web search: enable billing, then a `web_search` tool if still refused | S–M |
| 4 | 7e Zoom tool plus a fresh frame when the user starts speaking | M |
| 5 | 7g `highlight_element`: click-through overlay pointing at the real control | M–L |
| 6 | 7b Reconnect context: carry a conversation summary into a fresh session | M |
| 7 | 7a Diagnostics: log search state every session; optional saving of sent frames | S |

**Then Phase 6 continues (browser agent, write operations).** Spec:
`docs/PHASE6_BROWSER_AGENT.md`. 6a–6e are done; remaining:

- **6f** `fill_field`, `clear_field`, `focus_field` (must dispatch synthetic
  `input`/`change` events for React-controlled forms)
- **6g** `select_option` (native `<select>` plus ARIA comboboxes)
- **6h** `click_button`, `submit_form` — **confirmation-gated**, fail safe on decline
  and timeout
- **6i** Allowed-domain list plus an on-page monitoring indicator
- **6j** Hardening: tab closed or navigated mid-operation, proxy crash, app restart,
  full regression over `tools/sample-forms/`

7d and 7g produce element-finding and highlighting that 6f–6h should reuse rather
than duplicate.

## 7. How to work on this repo

**Build and test:**

```bash
dotnet build GeminiLiveShare.sln
dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj
```

`GeminiLiveShare.Tests` is a **console harness, not xUnit**. Each check is a method
in `Program.cs` that throws on failure. Add new checks the same way. Some tests call
the real Gemini API and need a key; they are the ones worth trusting for behavior
questions.

**Conventions in this codebase:**

- Comments explain *why*, usually with a measured number behind the decision. Keep
  that style; do not add narration comments.
- One phase equals one commit, with a `PHASE_LOG.md` entry recording cause,
  implementation, files changed, automated verification and manual test steps.
- Claims about model behavior are **measured against the live API**, not assumed.
  Several past bugs were mis-diagnosed until measured.
- Privacy rules are non-negotiable: never log audio, images, transcripts or keys;
  password fields are never readable or writable by any tool.

**The session log is the best debugging tool.** For any "Gemini did something
strange" report, read it first:

```bash
grep -iE "quota|search|resumption|dropped" "$LOCALAPPDATA/GeminiLiveShare/logs/session-$(date +%Y%m%d).log"
```

It shows frames sent, dropped and skipped, microphone loss, JPEG quality changes,
screen-share state and reconnects.

**Manual testing matters here.** Audio quality, echo and "does it point at the right
button" cannot be verified by automated tests. Every phase log entry ends with manual
steps for this reason.

## 8. Facts an agent will otherwise get wrong

1. The `README.md` "Phase 0 skeleton" description was stale for a long time. The app
   is feature-complete through Phase 5 plus 6a–6e.
2. `gemini-live-share-architecture.md` (repo root) is a **superseded duplicate** of
   `docs/ARCHITECTURE.md`. Ignore it.
3. `docs/ARCHITECTURE.md` claims sanitized frames are written to
   `C:\Temp\gemini-frames`. **The code no longer does this.**
4. The vision problem is **not** a capture-quality problem. Do not "fix" it by
   raising resolution or JPEG quality: that starves the microphone over the shared
   WebSocket and was already measured as harmful.
5. Web search failing is **not** a missing-`googleSearch`-tool bug. The tool is sent
   and refused for quota.
6. There is no test framework dependency. Do not add xUnit or NUnit.
