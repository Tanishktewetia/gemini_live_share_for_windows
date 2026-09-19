# PROJECT STATE — Gemini Live Share (Windows)

**Read this file first. It is the single source of truth for what exists today.**
Last updated: 2026-09-19 · Branch: `main` · Latest commit: `d55768e`

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
| Phase 7e zoom_region + fresh-frame forcing | Working; regular Gemini crop analysis plus Gemini-only A1-D4 grid |
| Phase 7f web search | Working through Live Google Search when available, with app `web_search` fallback when quota blocks Live grounding |
| Phase 7g element highlighting | Working; UI Automation/browser lookup plus click-through capture-excluded overlay |
| Diagnostics log | Working: `%LOCALAPPDATA%\GeminiLiveShare\logs\session-yyyyMMdd.log` |
| Browser agent 6a–6e | Working: extension ⇄ proxy ⇄ app pipe, `get_active_page`, `get_form_fields`, page context injected into the conversation. **Read-only** |
| Google Search in Live | **May be refused by key quota.** The client now falls back to app `web_search`; the UI/status reports which capability is active. See §5 |

## 5. Known problems (the current work)

These were diagnosed on 2026-09-17/18 with real sessions and the diagnostics log.

### P1 — Gemini invents details it cannot see (addressed by Phase 7)
The original failure was invented taskbar/desktop counts, pointer locations, and small
visual details. Phase 7 now routes exact desktop facts through UI Automation, uses
`zoom_region` for details that need vision, and instructs the model to refuse weak
evidence. Live/manual verification remains important for unusual applications.

**Cause: not the capture pipeline.** The log shows frames delivered well: 160 sent,
0 dropped, ~228 KB each at quality 90, 2 ms uploads. The Live API compresses every
video frame to a small fixed size, so 24 px icons and small text become unreadable.
The model then fills the gap with a plausible guess. This is why the same task works
on a phone (large UI) and fails on a desktop (dense UI).

**Implemented direction:** stop using vision for things Windows can answer exactly.
Use UI Automation for elements, the taskbar and desktop icons; use `zoom_region`
for the remaining visual details; and instruct the model never to guess.
Details are recorded in `PHASE_LOG.md`.

### P2 — Live Google Search can be refused by API-key quota
Live Google Search grounding is attempted first. If the key lacks the required quota,
the client falls back to the app-level `web_search` function tool, which uses a
regular Gemini request with Google Search grounding. The UI shows whether search is
using Live grounding, the app fallback, or is unavailable. Billing/quota changes are
still external to this repository.

### P3 — Element highlighting
Resolved in Phase 7g. `highlight_element` finds visible enabled controls through UI
Automation first, then the existing browser integration when available, and shows a
temporary click-through marker. Manual DPI, multi-monitor, and browser verification
are still required.

### P4 — Context across fresh sessions
Resolved in code by `ConversationStateRebuilder`: when reconnecting into a fresh Live
session, the app restores recent turns and current screen-share/page-context state and
shows a Reconnected badge. Manual network-drop verification remains.

## 6. What's next

**Current phase: Phase 7 — Accuracy and reliability.** Full detail, with "done when"
criteria for each step, is in `docs/PHASE7_ACCURACY_PLAN.md`.

| Order | Step | Size |
|---|---|---|
| 1 | 7c System instruction: never guess; ask the user to point | Implemented |
| 2 | 7d UI Automation tools: element under cursor, taskbar, desktop icons, focused window | Implemented |
| 3 | 7f Web search: Live grounding plus app `web_search` fallback | Implemented |
| 4 | 7e Zoom tool plus a fresh frame when the user starts speaking | Implemented |
| 5 | 7g `highlight_element`: click-through overlay pointing at the real control | Implemented; manual DPI/browser verification remains |
| 6 | 7b Reconnect context: carry a conversation summary into a fresh session | Implemented |
| 7 | 7a Diagnostics: log search state every session; optional saving of sent frames | Implemented |

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
