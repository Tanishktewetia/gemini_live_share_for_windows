# Phase Log

## Phase 7 corrective fix - Live tools were rejected, so highlight and fallback search never ran

- **Date:** 2026-09-19
- **Status:** Corrected in code; automated verification passes. Real-app verification is still pending after restart.
- **Evidence from the user's 2026-09-19 session:**
  - The exported conversation shows Gemini saying it highlighted Downloads, then admitting that no visible highlight was created. This was a model claim, not a successful tool result.
  - The session log repeatedly records `Unknown name "additionalProperties" at 'setup.tools[...].function_declarations[...].parameters'` and then falls back to `desktop tools OFF`.
  - Because the Live function declarations were rejected, `highlight_element`, `zoom_region`, and desktop automation tools were never available to Gemini in that session.
  - The same fallback left the app with Live search OFF and no callable app-level `web_search` tool, which explains the `Web search unavailable` badge and the fabricated/pseudo search attempts in the transcript.
- **Fix:**
  - Removed unsupported `additionalProperties`, `minItems`, and `maxItems` fields from Live function schemas.
  - Ensured the no-Google fallback always exposes the app-level `web_search` function, even if desktop tools are unavailable.
  - Added explicit system-instruction rules: call `highlight_element` before claiming a highlight, and call `web_search` before claiming an internet search.
  - Added schema tests that fail if unsupported fields return or if the no-desktop fallback omits `web_search`.
  - Added diagnostics logging for received tool names, so a real tool call can be distinguished from model text.
  - Strengthened the highlight window's native `WM_NCHITTEST` handling so clicks pass through to the underlying application.
  - Removed the accidental browser-agent element-lookup additions from the previous Phase 7 commit. Browser-agent functionality was not extended in this corrective work.
- **Verification:**
  - `dotnet build GeminiLiveShare.sln --no-restore -p:BaseOutputPath=build-check\` — pass, 0 warnings, 0 errors.
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj --no-build -p:BaseOutputPath=build-check\` — pass.
  - `node --check extension/background/service-worker.js` — pass; the existing browser-agent file is restored to its prior state.
- **Acceptance condition:** Do not call Phase 7 complete until a restarted app produces a real `highlight_element` tool call and a real `web_search` tool call or a clearly reported provider-level failure.

## Phase 7e/7f/7g - zoomed vision, fallback web search, and click-through highlighting

- **Date:** 2026-09-19
- **Status:** Implemented; solution build and console harness pass.
- **Goal:** Complete the remaining accuracy UX needed for small visual details, current-information requests, and non-technical click guidance.
- **Implementation:**
  - **7e zoom:**
    - Retained the latest full-resolution, privacy-sanitized JPEG separately from the upload-sized Live frame.
    - Added `zoom_region(cells | box, question)` tool execution through a regular Gemini `generateContent` request with high media resolution.
    - Added an A1-D4 grid to the Gemini-only upload image; the user screen and full-resolution zoom source are never modified.
    - A user transcription forces the next unchanged frame to be captured, and a pending zoom request waits briefly for that fresh frame instead of answering from stale pixels.
  - **7f web search:**
    - Added a `web_search(query)` Live function-tool fallback for keys where Live Google Search grounding is refused for quota/billing reasons.
    - The fallback calls regular Gemini with Google Search grounding, returns a concise answer plus source titles/URLs, and fails closed when the request cannot be completed.
    - Search capability is surfaced in session status as Google Search, app `web_search`, or OFF; existing quota caching/fallback behavior remains.
  - **7g highlight:**
    - Added `highlight_element(name, role)` with foreground-window UI Automation lookup. Browser-agent code was intentionally left out of this corrective scope.
    - Added a transparent, non-activating, click-through WPF overlay with explicit native hit-test passthrough, DPI-aware coordinate conversion, multi-monitor screen coordinates, 8-second auto-clear, left-click clear, and `WDA_EXCLUDEFROMCAPTURE`.
    - The overlay clears when the captured screen changes, screen sharing stops, or the session ends. It never clicks the target.
    - Added browser `find_element` support returning visible enabled element bounds in physical screen pixels.
- **Files changed:**
  - `src/GeminiLiveShare.Core/Vision/ImageProcessingService.cs`
  - `src/GeminiLiveShare.Core/Gemini/GeminiLiveClient.cs`
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.Core/Gemini/GeminiWebSearchService.cs`
  - `src/GeminiLiveShare.Core/Gemini/IWebSearchService.cs`
  - `src/GeminiLiveShare.Core/Desktop/HighlightOverlayService.cs`
  - `src/GeminiLiveShare.Core/Desktop/IHighlightOverlayService.cs`
  - `src/GeminiLiveShare.Core/Desktop/DesktopAutomationService.cs`
  - `src/GeminiLiveShare.App/App.xaml.cs`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` (pass; 0 errors)
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` (pass)
  - Added harness coverage for `highlight_element`, `web_search`, zoom execution, and fresh-frame forcing.
- **Manual verification still required:**
  1. With Windows display scaling at 100% and 150%, say "click Next" in Windows Settings; verify the marker is exactly over the control and the click goes through.
  2. With a supported browser form open, say "highlight the Email field"; verify browser fallback coordinates and click-through behavior.
  3. Ask for small text in an image/PDF; verify the answer comes from the zoomed crop and the A1-D4 grid is not visible on the user's screen.
  4. Ask a current web question with a key that lacks Live Search quota; verify the app uses `web_search`, reports sources, and never claims a search when the fallback fails.

## Phase 7d/7e hotfix - block deterministic counts until visual context is active

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Issue:** User reported contradictory flow where Gemini said screen was unavailable but still returned deterministic desktop icon counts.
- **Fix:**
  - Deterministic count routing now requires active visual context:
    - screen share ON
    - first screenshot actually sent (screen-share notice pending cleared)
  - If count is requested before visual context is active, app sends authoritative no-screen reply (`GeminiLiveClient.NoScreenReply`) instead of returning a count.
  - Clearing count intent/expectation state when screen share is turned OFF to avoid stale carryover.
- **Files changed:**
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` (pass)
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` (pass)
  - Added test: `ValidateCountIntentRequiresVisualContextAsync`

## Phase 7d audio reliability hotfix - assistant silence watchdog + audible deterministic count delivery

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Issue:** Users reported non-audible assistant turns (partial/empty reply), while chat-only updates were insufficient for overlay-first usage.
- **Fix:**
  - Deterministic desktop/taskbar count answers now route through Gemini with an authoritative **"say exactly"** prompt so the response is spoken.
  - Added assistant-audio watchdog in `SessionOrchestrator`:
    - starts on user transcription
    - marks success on first assistant audio chunk
    - if no assistant audio within timeout, triggers one recovery cycle (disconnect/reconnect/re-ask request)
    - cooldown prevents rapid recovery loops
  - Added turn-complete silent-turn check and explicit status/diagnostic logging for recovery attempts.
  - Pending expectation handling updated so post-deterministic drift replies are suppressed.
- **Files changed:**
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` (pass)
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` (pass)
## Phase 7d count UX hotfix - deterministic counts now spoken via Gemini audio, plus follow-up and reliability guards

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Issue reported:** Count answers appeared in chat but were not spoken reliably on overlay-first usage, and follow-up count prompts still drifted.
- **Fix:**
  - Deterministic local count path now sends an authoritative "say exactly" prompt to Gemini so the answer is spoken, rather than only inserting a local assistant row.
  - Added recent count-intent memory for follow-ups ("are you sure...", "just give total...") so they keep deterministic target routing.
  - Kept no-guess reliability guard: unreliable desktop/taskbar selectors produce explicit "can't verify exact count" spoken response.
  - Improved desktop/taskbar payloads with reliability + strategy metadata for diagnostics/tool consumers.
- **Files changed:**
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.Core/Desktop/DesktopAutomationService.cs`
  - `src/GeminiLiveShare.Core/Desktop/IDesktopAutomationService.cs`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` (pass)
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` (pass)
## Phase 7d/7e count reliability hardening - multi-strategy desktop/taskbar counts + no-guess guard

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Goal:** Eliminate catastrophic wrong icon counts (especially false zero) and follow-up drift after deterministic count answers.
- **Implementation:**
  - **Desktop count robustness (`DesktopAutomationService`):**
    - Added Win32 desktop host discovery strategies (`Progman` + `WorkerW` -> `SHELLDLL_DefView` -> `SysListView32`) instead of relying only on UIA Name=`FolderView`.
    - Primary count remains shell list-view item count (`LVM_GETITEMCOUNT`) when available.
    - Added UIA visible-item fallback for labels/positions and count fallback when shell count is unavailable.
    - Added reliability metadata (`IsReliable`, `ReliabilityNote`, `SourceStrategy`) to desktop count snapshot.
  - **Taskbar count robustness (`DesktopAutomationService`):**
    - Kept `MSTaskListWClass` app-button strategy as primary.
    - Added fallback strategy: filtered visible taskbar buttons excluding tray descendants and reserved system keywords.
    - Added reliability metadata + source strategy and kept diagnostics breakdown (`app/tray/system`).
  - **No-guess guard in deterministic local replies (`SessionOrchestrator`):**
    - If count snapshot is unreliable, app now replies with explicit "cannot verify exact count" instead of returning `0`.
    - Deterministic count path now logs strategy + reliability details in diagnostics.
  - **Follow-up count intent handling (`SessionOrchestrator`):**
    - Added recent count-intent memory (45 s) so prompts like "Are you sure there are 42 icons?" are treated as deterministic re-counts instead of model guesses.
  - **Assistant-drift suppression:**
    - After a deterministic local count reply, the next model count reply is suppressed to avoid contradictory duplicates in history.
- **Files changed:**
  - `src/GeminiLiveShare.Core/Desktop/IDesktopAutomationService.cs`
  - `src/GeminiLiveShare.Core/Desktop/DesktopAutomationService.cs`
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` (pass)
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` (pass)
  - Added/updated tests:
    - `ValidateDesktopIntentGroundingAsync`
    - `ValidateCountFollowUpUsesRecentTargetAsync`
    - `ValidateUnreliableCountGuardAsync`
## Phase 7d counting hardening follow-up - deterministic local counts + scoped policies + diagnostics

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Goal:** Fix persistent icon-count errors by removing model inference from count answers and tightening count data sources/policies.
- **Implementation:**
  - `SessionOrchestrator` now handles desktop/taskbar count intents deterministically in-app:
    - saves user turn
    - computes count locally via desktop automation
    - writes assistant reply directly to chat history (no model guessing)
    - logs count diagnostics breakdown
  - Added count-reply suppression guard so a trailing model count reply does not reintroduce contradictory history.
  - Updated taskbar count policy/data source in `DesktopAutomationService`:
    - app count comes from `MSTaskListWClass` buttons only
    - excludes Start/Search/Widgets/tray/clock/overflow from final count
    - diagnostics fields include app/tray/system button counts
  - Updated desktop count source in `DesktopAutomationService`:
    - primary count from shell desktop list view (`LVM_GETITEMCOUNT` on FolderView handle)
    - UIA icon list retained for labels/positions only
    - diagnostics fields include source/visible/hidden-or-filtered values
  - Extended desktop automation contracts with explicit count snapshots and policy text:
    - `DesktopIconCountSnapshot`
    - `TaskbarItemCountSnapshot`
- **Files changed:**
  - `src/GeminiLiveShare.Core/Desktop/IDesktopAutomationService.cs`
  - `src/GeminiLiveShare.Core/Desktop/DesktopAutomationService.cs`
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` (pass)
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` (pass)
## UX follow-up - export chat menu with markdown save + copy full chat

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Request:** When clicking **Export chat**, offer two actions: save `.md` and copy the full conversation.
- **Implementation:**
  - Reworked export click handler to show an action menu with:
    - **Save as .md file** (file-save dialog limited to Markdown)
    - **Copy full chat** (copies full markdown transcript to system clipboard)
  - Kept existing transcript formatting and status feedback messages.
- **Files changed:**
  - `src/GeminiLiveShare.App/Views/MainWindow.xaml.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` (pass)
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` (pass)
Running record of every fix and phase verification, newest first. Each entry lists status, cause,
implementation, files changed, verification performed, and manual test steps.

> This is history, not current state. For what the project is and where it stands today, read
> [`PROJECT_STATE.md`](PROJECT_STATE.md). Open this file when you need the measurements and
> reasoning behind a past fix.

## Phase 7d hardening - authoritative desktop intent routing + assistant correction

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Goal:** Make desktop-icon/taskbar counting deterministic and eliminate approximate/guessed assistant replies for these intents.
- **Implementation:**
  - Added desktop intent router in SessionOrchestrator for user queries like:
    - desktop icon count
    - taskbar item count
    - pointer/cursor location
  - For matched intents, app now sends authoritative UI Automation facts immediately to Gemini (exact counts/coordinates), with strict instruction text to avoid estimates.
  - Added pending expectation tracking for count intents and an assistant-response validator:
    - if Gemini replies with missing/mismatched count, app sends correction turn with exact value and requests re-answer
    - mismatched assistant reply is suppressed from chat history
    - corrected exact reply is persisted
  - Added lifecycle cleanup for pending expectations on start/stop.
  - Added automated harness validation (ValidateDesktopIntentGroundingAsync) proving:
    - authoritative count context is sent
    - mismatched assistant count triggers correction
    - mismatched count is not persisted
    - corrected exact count is persisted
- **Files changed:**
  - src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs
  - src/GeminiLiveShare.Tests/Program.cs
- **Verification:**
  - dotnet build GeminiLiveShare.sln (pass)
  - dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj (pass)
## Phase 7d hotfix - start fallback when Live tool setup is rejected

- **Date:** 2026-09-19
- **Status:** Implemented; build and tests pass.
- **Issue:** After 7d tool wiring, some sessions failed during setup and never reached conversation start (connected briefly then disconnected).
- **Fix:**
  - Added setup fallback chain in GeminiLiveClient:
    1. web search + desktop tools
    2. desktop tools only
    3. no tools
  - If any richer tool setup is rejected by server, client now retries with a reduced set instead of failing start.
  - Status output now includes desktop-tools ON/OFF and logs fallback attempts.
- **Files changed:**
  - src/GeminiLiveShare.Core/Gemini/GeminiLiveClient.cs
- **Verification:**
  - dotnet build GeminiLiveShare.sln (pass)
  - dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj (pass)
## Phase 7d - Live desktop automation tools wired into Gemini session

- **Date:** 2026-09-19
- **Status:** Implemented (first end-to-end wiring); build and tests pass.
- **Goal:** Replace vision guessing for pointer/location/counting questions with exact Windows UI Automation data.
- **Implementation:**
  - Added IDesktopAutomationService + DesktopAutomationService as the Windows/UIA backend for:
    - get_element_under_cursor
    - list_taskbar_items
    - list_desktop_icons
    - get_focused_window
  - Extended Live setup tool declarations to expose the above functions to Gemini in every session.
  - Extended Live message parsing to read server 	oolCall.functionCalls payloads.
  - Added client-side tool response transport (	oolResponse.functionResponses) so tool outputs return to Gemini.
  - Wired SessionOrchestrator to execute incoming desktop tool calls and respond with structured JSON (name/type/path/bounds, counts, focused window metadata).
  - Updated system instruction to explicitly list available desktop tools.
  - Expanded protocol tests to verify tool-call parsing and setup tool serialization with/without web search.
- **Files changed:**
  - src/GeminiLiveShare.Core/Desktop/IDesktopAutomationService.cs
  - src/GeminiLiveShare.Core/Desktop/DesktopAutomationService.cs
  - src/GeminiLiveShare.Core/Gemini/GeminiLiveClient.cs
  - src/GeminiLiveShare.Core/Gemini/IGeminiLiveClient.cs
  - src/GeminiLiveShare.Core/Gemini/ToolCallsEventArgs.cs (new)
  - src/GeminiLiveShare.Core/Gemini/Models/SetupMessage.cs
  - src/GeminiLiveShare.Core/Gemini/Models/ServerMessageParser.cs
  - src/GeminiLiveShare.Core/Gemini/Models/ToolResponseMessage.cs (new)
  - src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs
  - src/GeminiLiveShare.Tests/Program.cs
- **Verification:**
  - dotnet build GeminiLiveShare.sln (pass)
  - dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj (pass)
## Phase 7c - system instruction hardening (never guess + tool-first + date/model context)

- **Date:** 2026-09-19
- **Status:** Implemented; build and test harness pass.
- **Goal:** Reduce invented visual answers by making instruction rules explicit for ambiguity-heavy tasks.
- **Implementation:**
  - Updated GeminiLiveClient.BuildInstruction(...) to include a **Session Context** header with:
    - Active model name (models/gemini-3.1-flash-live-preview)
    - User-local instruction build date (yyyy-MM-dd) and UTC offset
    - Guidance to use that date for relative terms (today/tomorrow/yesterday)
  - Tightened DesktopVisionInstruction with a new **Accuracy and reliability rules** section:
    - "Never guess" baseline rule
    - Tool-first requirement for pointer location, counting, small-text reading, and icon/control identification
    - Explicit fallback: if no tool fits, say it cannot see clearly instead of inventing
    - Clarification rule: ask user to point with the mouse when the on-screen target is unclear
  - Extended tests (ValidateWebSearchSetup) with assertions for:
    - never-guess wording
    - tool-first wording
    - mouse-point clarification wording
    - model/date context using a fixed timestamp
- **Files changed:**
  - src/GeminiLiveShare.Core/Gemini/GeminiLiveClient.cs
  - src/GeminiLiveShare.Tests/Program.cs
- **Verification:**
  - dotnet build GeminiLiveShare.sln (pass)
  - dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj (pass)
## Phase 7a settings UX follow-up - diagnostics toggle and direct log paths

- **Date:** 2026-09-19
- **Status:** Implemented; build and test harness pass.
- **Request:** In Settings, let users toggle image/frame saving on/off and provide direct access to log/frame paths.
- **Implementation:**
  - Diagnostics card now includes:
    - `Save sent screen frames (debug)` toggle (on/off persists locally)
    - Displayed frames folder path
    - **Open Frames Folder** button
    - Displayed logs folder path and today's session log path
    - **Open Logs Folder** and **Open Today's Log** buttons
  - App now passes the same configured frames path into `FileSessionDiagnostics` to keep writer path aligned with the settings path.
  - Opening paths creates missing folders (and an empty today-log file if needed), then launches with the OS shell.
- **Files changed:**
  - `src/GeminiLiveShare.App/Views/MainWindow.xaml`
  - `src/GeminiLiveShare.App/Views/MainWindow.xaml.cs`
  - `src/GeminiLiveShare.App/ViewModels/SettingsViewModel.cs`
  - `src/GeminiLiveShare.App/App.xaml.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` ✅
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` ✅
## Phase 7a follow-up - frame path alignment + message timestamps

- **Date:** 2026-09-18
- **Status:** Implemented; build and tests pass.
- **Why:** Manual run showed confusion about missing saved frames and difficulty quantifying perceived latency from chat bubbles.
- **Changes:**
  - Aligned diagnostics frame output path between settings and writer: both now use `%LOCALAPPDATA%\GeminiLiveShare\logs\sent-frames`.
  - `FileSessionDiagnostics` now accepts an explicit `sentFramesDirectory`; app wires it from `DiagnosticsDebugSettings`.
  - Added session-start log line for frame-capture setting and active path (`save-sent-frames on/off`).
  - Added per-message timestamps in chat UI (`h:mm:ss tt`) so response delay is visible directly in conversation history.
- **Files changed:**
  - `src/GeminiLiveShare.Core/Diagnostics/DiagnosticsDebugSettings.cs`
  - `src/GeminiLiveShare.Core/Diagnostics/SessionDiagnostics.cs`
  - `src/GeminiLiveShare.App/App.xaml.cs`
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.App/ViewModels/ChatHistoryViewModels.cs`
  - `src/GeminiLiveShare.App/Views/MainWindow.xaml`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Verification:**
  - `dotnet build GeminiLiveShare.sln` ✅
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` ✅
## Phase 7a/7b - Diagnostics visibility + reconnect context restore

- **Date:** 2026-09-18
- **Status:** Implemented; build and test harness pass.
- **Goal:** Make reconnect/search failures visible in logs and UI (7a), and stop conversation context loss when reconnect falls back to a fresh Live session (7b).
- **Implementation:**
  - Added structured reconnect/session-setup telemetry in `GeminiLiveClient` and surfaced it via new `SessionReady` event (`SessionReadyEventArgs`): reconnect vs first connect, resumption attempted/succeeded, and web-search on/off.
  - `SessionOrchestrator` now logs every setup/reconnect state and tracks `IsWebSearchAvailable` / `HasReconnected` for the UI.
  - Added reconnect context restoration via `ConversationStateRebuilder`: when resumption is rejected and a fresh session is created, it sends a compact "continue this conversation" notice with recent turns plus current screen-share state and optional browser-page context.
  - Added debug diagnostics setting `SaveSentFrames` (`DiagnosticsDebugSettings`) and frame persistence in `FileSessionDiagnostics.SaveSentFrame(...)`; saved JPEGs are post-filter frames exactly as sent to Gemini.
  - Added UI indicators in the main header: **Web search unavailable** and **Reconnected**.
  - Added settings toggle under Diagnostics to enable/disable frame capture and show save path.
- **Files changed:**
  - `src/GeminiLiveShare.Core/Gemini/GeminiLiveClient.cs`
  - `src/GeminiLiveShare.Core/Gemini/IGeminiLiveClient.cs`
  - `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`
  - `src/GeminiLiveShare.Core/Gemini/ConversationStateRebuilder.cs` (new)
  - `src/GeminiLiveShare.Core/Gemini/SessionReadyEventArgs.cs` (new)
  - `src/GeminiLiveShare.Core/Diagnostics/SessionDiagnostics.cs`
  - `src/GeminiLiveShare.Core/Diagnostics/DiagnosticsDebugSettings.cs` (new)
  - `src/GeminiLiveShare.App/ViewModels/MainViewModel.cs`
  - `src/GeminiLiveShare.App/ViewModels/SettingsViewModel.cs`
  - `src/GeminiLiveShare.App/Views/MainWindow.xaml`
  - `src/GeminiLiveShare.App/Views/MainWindow.xaml.cs`
  - `src/GeminiLiveShare.App/App.xaml.cs`
  - `src/GeminiLiveShare.Tests/Program.cs`
- **Automated verification:**
  - `dotnet build GeminiLiveShare.sln` ✅
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj` ✅
  - New tests:
    - `ValidateReconnectContextRestoreAsync` (fresh-session reconnect restores recent context)
    - `ValidateSentFrameDiagnosticsAsync` (debug frame capture writes sent JPEGs)
- **Manual verification (to run):**
  1. Start conversation, disconnect network briefly, reconnect; verify status shows **Reconnected** and Gemini continues without a fresh greeting.
  2. If Live search quota is unavailable, verify header shows **Web search unavailable**.
  3. Enable "Save sent screen frames (debug)" and confirm JPEGs appear under the shown diagnostics path while sharing is on.
## Diagnosis - Invented screen details, no web search (2026-09-17/18)

- **Status:** Diagnosed from real sessions and the diagnostics log. Fixes planned as Phase 7, not yet implemented.
- **Reported:** Gemini named taskbar apps that were not on screen (Edge, Teams, VS Code), guessed the mouse pointer's
  location, gave a made-up desktop icon count ("42") and a made-up icon position, and said it had no internet access.
- **Evidence gathered:**
  - Session log `session-20260917.log`: 160 frames sent, **0 dropped**, ~228 KB each, JPEG quality 90, uploads 2 ms
    average. The capture pipeline is healthy, so lost detail is not local.
  - Same log, twice (21:35:59 and 22:07:16): `Web search is not available for this API key (no quota)`. The server
    rejects the `googleSearch` setup in ~400 ms; the code sending it is correct.
  - Google AI Studio console: project on the **Free tier**, with 409/429 errors spiking on Sep 17. The Live API itself
    shows "Unlimited" RPM, so Live is not the constraint; search grounding is a separate paid capability.
  - The mid-conversation "Hello! How can I help you today?" was **not** a failed session resumption, as first suspected.
    The log shows a deliberate disconnect/reconnect at 21:39:12 followed by `Screen share OFF; visual context cleared`:
    a new conversation being started.
- **Cause of the invented details:** the Live API compresses each video frame to a small fixed size, so small icons and
  text are unreadable by the time the model sees them, and it fills the gap with a plausible guess. Sending larger
  frames is not a fix: it starves the microphone over the shared WebSocket (see the Production Hardening entry below).
- **Planned fix:** `docs/PHASE7_ACCURACY_PLAN.md` - answer from UI Automation instead of vision where possible, add a
  zoom tool for the rest, instruct the model never to guess, and add an on-screen highlight so it can point at controls.

## Production Hardening - Screen-share bandwidth, broken voice, web search, app naming, header title

- **Date:** 2026-09-17
- **Status:** Implemented; automated tests pass; awaiting manual test.
- **Product goal (from the user):** a voice assistant that watches the screen and guides people, especially older users, through computer tasks. It must hear clearly, see accurately, never invent what it sees or does, and stay smooth on ordinary home internet.
- **Reported problems:**
  1. With screen sharing on, Gemini said the user was in "VS Code". The screen showed the Claude desktop app.
  2. The user said "Claude"; transcripts show "Cloud" / "Cloud Code" / "Cloud Core".
  3. Asked to search the internet for Anthropic, Gemini replied "I searched Anthropic…". No search tool existed.
  4. Gemini's voice broke up during the conversation.
  5. The conversation header kept an old title ("हिंदी में बातचीत") while the sidebar showed the new one.

### 1. Screen frames were oversized and starved the microphone (voice breaking, misheard words)
- **Measured locally (real capture pipeline, nothing sent):** a 1920×1080 screen was **upscaled to 2048×1152** (`MinimumOutputWidth = 2048`) and encoded at **JPEG q98**. That is **537 KB per frame (base64)**, about **4.4 Mbit/s of upload at 1 fps**, against ~0.3 Mbit/s for audio. Processing took ~105 ms per frame, which is fine on 16 CPUs. At native resolution: q98 386 KB, q92 255 KB, **q90 238 KB**, q85 202 KB, q80 178 KB (JPEG bytes).
- **Mechanism:** frames and microphone audio share one WebSocket send lock, and one 537 KB frame holds it for its whole upload. The microphone queue held only 2 chunks (80 ms) and discarded the oldest.
- **Measured on the live API** (text-heavy synthetic frames at realistic sizes, not the user's screen; the app's exact mic queue replayed with 40 ms chunks):

  | Condition | Frame upload avg / max | Mic audio lost | Gemini voice |
  |---|---|---|---|
  | No screen share | – | 1.2% | smooth |
  | Current frames (525 KB) | 232 / 382 ms | **11.1%** | smooth |
  | Native q90 frames (315 KB) | 65 / 220 ms | **0.3%** | smooth |
  | Native q90, during a network stall | 399 / **2254 ms** | 15.9% | **2.4 s silent** |

  Losing ~11% of speech explains words being misheard. The last row shows this connection also has occasional ~2 s upload stalls, which silence Gemini's reply. The app can't prevent network stalls, but it now avoids adding to them.
- **Fix:**
  - `ImageProcessingService`: native resolution (never upscale; only captures wider than 2560 px are reduced), default JPEG quality 90.
  - **Change detection:** the sanitized frame is compared with the last sent frame at 480×270 grayscale, per pixel (>8 levels, ≥2 pixels). An unchanged screen is skipped, but refreshed at least every 5 s. A single typed character counts as a change and is sent immediately. Detection resets whenever sharing (re)starts, so the first frame is always sent.
  - **Adaptive quality** (`SessionOrchestrator.RecordFrameUpload`): an upload ≥400 ms lowers quality by 10 (floor 60); 5 consecutive uploads ≤150 ms raise it back toward 90.
  - **Microphone queue** raised from 2 to 13 chunks (~520 ms): a normal frame upload now delays speech briefly instead of deleting it, and latency is still bounded after a real stall.
  - `IImageProcessingService.EncodeForGeminiAsync` now returns `FrameEncodeResult` (Encoded / Unchanged / Dropped).
- **Diagnostics log:** new `FileSessionDiagnostics` writes `%LOCALAPPDATA%\GeminiLiveShare\logs\session-yyyyMMdd.log` (keeps 14 days). It records status events plus a summary every 30 s and at session end: mic chunks captured/lost (%), echo cancellation on/off, frames sent/unchanged-skipped/dropped, average KB, upload avg/max ms, and JPEG quality changes. It never logs audio, images, transcripts or keys.

### 2. Web search was never enabled, and the key has no quota for it
- **Cause:** the Live setup declared no tools, so "I searched…" was invented.
- **Measured:** adding `tools: [{ googleSearch: {} }]` makes the server close the setup with **"You exceeded your current quota, please check your plan and billing details"**. The identical setup without tools returns `setupComplete`. This API key has no Google Search quota for Live sessions, so unconditionally enabling search would have broken every conversation.
- **Found while testing:** a setup rejected by the server was reported only after the **15 s setup timeout** as a generic timeout, because the receive task's error never completed the setup wait. Setup now waits for either completion or the receive task, so real errors surface immediately.
- **Fix:** `GeminiLiveClient` requests Google Search first. On a quota rejection it reconnects immediately without tools, remembers that for the rest of the app run, and shows "Web search is not available for this API key (no quota); continuing without it". The instruction matches the capability: with search, use it for lookups; without search, never claim to have searched, say searching isn't possible, and answer from knowledge with a caveat.
- **Verified with the real client:** first connect detected the rejection at 0.9 s and connected without search at 1.8 s; the second connect skipped search and connected in 1.3 s.
- **Quota note:** repeated live tests in this session likely used much of this key's free allowance, so later live tests were kept to handshakes only.

### 3. App identification ("VS Code") and name recognition ("Cloud")
- **Cause:** the model guessed the app from its look (Claude's desktop app resembles an IDE). Speech recognition turns "Claude" into "Cloud".
- **Fix (instruction):** identify apps from visible text (window/tab titles, taskbar or menu labels), not layout or colours; say so when unsure. A "names you may hear" note maps "Cloud"/"Cloud Code" said about an AI app to Claude/Claude Code by Anthropic, and tells Gemini to adopt user corrections. The recovered microphone audio (section 1) should also reduce mishearing.

### 4. Conversation header showed a stale title
- **Cause:** `MainViewModel.SessionHeader` was only refreshed when a different conversation was selected, not when the open conversation's title changed (generated early/final title or rename).
- **Fix:** subscribe to the selected session's `HeaderText` changes.

- **Files changed:** `src/GeminiLiveShare.Core/Vision/IImageProcessingService.cs`, `src/GeminiLiveShare.Core/Vision/ImageProcessingService.cs`, `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`, `src/GeminiLiveShare.Core/Gemini/GeminiLiveClient.cs`, `src/GeminiLiveShare.Core/Gemini/Models/SetupMessage.cs`, `src/GeminiLiveShare.Core/Diagnostics/SessionDiagnostics.cs` (new), `src/GeminiLiveShare.App/App.xaml.cs`, `src/GeminiLiveShare.App/ViewModels/MainViewModel.cs`, `src/GeminiLiveShare.Tests/Program.cs`.
- **Automated verification:**
  - New `ValidateFrameResolutionAndChangeDetectionAsync`: 1080p is sent at 1920×1080; an identical frame is skipped; an 8×14 px "typed character" is detected and sent; the first frame after a reset is sent; 4K is sent at 2560×1440; lower quality produces fewer bytes.
  - New `ValidateWebSearchSetup`: tools are serialized only when enabled; the measured quota message is recognised and an ordinary failure is not; both instruction variants keep the screen rules and have the correct search wording.
  - Solution builds with 0 errors; all tests pass.
- **Manual verification (to do):**
  1. Start a conversation, turn on screen share, and talk for 2–3 minutes while using the PC. The voice should stay clear both ways.
  2. Open the log in `%LOCALAPPDATA%\GeminiLiveShare\logs\` and check "mic lost" is near 0%. Frames should be ~150–300 KB, with "unchanged-skipped" rising while the screen is still.
  3. With the Claude app on screen, ask "which application is open?". Gemini should name it from visible text or say it isn't sure, not guess VS Code.
  4. Say "Claude" in a sentence and check Gemini understands it as Claude.
  5. Ask "search the internet for Anthropic". Status should show that web search isn't available for this key, and Gemini must say it can't search rather than claim it did.
  6. Watch the header while a conversation gets its title. It should update together with the sidebar.

## Phase 2 Fix - Gemini claims to see the screen while screen sharing is off

- **Date:** 2026-09-17
- **Status:** Implemented and verified with the live API; awaiting manual test.
- **Reported bug:** Screen sharing was never turned on, yet when asked "what do you see on my screen?" Gemini said it was watching the desktop and described icons.
- **Investigation:**
  - **Transcript** (chat history DB, session titled "Counting Desktop Icons"): Gemini said "Yes, I do. I'm seeing your desktop right now", asked the user to "zoom in a bit" because labels were "blurry", then reported "a total of 25 icons", named "Chrome, Postman, Slack", and said there was no "cursor" icon.
  - **Ground truth:** the real desktop folders contain ~57 items, **including `Cursor.lnk`** and **no Slack**. The answer was invented, not read from an image.
  - **Pipeline audit:** screen frames can only be sent through `SessionOrchestrator.StartVideoSender`, reached only when `_screenShareDesired` is true, which is set only by the overlay's screen-share button (`OverlayWindow.OnScreenShareClick` → `SetScreenShareEnabledAsync`). `StartAsync` forces it off. No leak path exists.
  - **Controlled reproduction:** fresh Live sessions with the real client and **zero frames sent**, asking "Do you see my screen?" and "How many icons are on my desktop?". **3 of 4 sessions claimed to see the screen**, inventing "a file explorer and a web browser", "8 desktop icons", and "a Canva restaurant template". One asked to "zoom in", matching the user's session.
- **Cause:** a model hallucination driven by the system instruction. It introduced Gemini as "a real-time desktop vision assistant" whose "realtime video stream contains screenshots from the user's primary monitor", gave detailed icon-counting advice (including "ask for a closer screenshot", the "zoom in" line), and never stated that screen sharing starts off. The model assumed visual access and made up the content. No image was sent.
- **Implementation:**
  - Rewrote `GeminiLiveClient.DesktopVisionInstruction` with top-priority screen access rules. Screen sharing is OFF when the conversation starts. Gemini can see only after the app says sharing is on AND a screenshot has actually been received. It must never describe or guess windows, apps, icons, text or counts without an image. With no screenshot it gives a fixed reply (`NoScreenReply`: "I can't see your screen right now. Turn on screen sharing with the screen button on the overlay…"). The screenshot-reading and icon-counting guidance applies only when screenshots are present.
  - `SessionOrchestrator` sends `ScreenShareOnNotice` right after the first screenshot of each sharing period has actually been sent, never before and never when the privacy filter drops frames. The status bar shows "Screen share ON: first screenshot sent to Gemini".
  - The two screen-off messages now use the same `NoScreenReply` wording.
- **Files changed:** `src/GeminiLiveShare.Core/Gemini/GeminiLiveClient.cs`, `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`, `src/GeminiLiveShare.Tests/Program.cs`.
- **Automated verification:**
  - Live API, sharing off, zero frames: **0 of 6 sessions** claimed to see the screen (before: 3 of 4). All replied honestly and pointed to the screen button.
  - Live API, sharing on, synthetic 1920×1080 desktop with 7 labelled icons (not the user's real screen), sent through `SendVideoFrameAsync` plus the notice: Gemini replied "I can see your screen now", then "There are 7 icons…" and **named all 7 labels correctly**.
  - New `ValidateScreenShareNoticeFollowsRealFrameAsync` test: nothing is sent before sharing is enabled; no notice when every frame is dropped; the notice immediately follows the first real frame and is sent once.
  - Solution builds with 0 errors; the test harness passes.
- **Doc drift noted:** `docs/ARCHITECTURE.md` (Phase 3b status) says sanitized frames are saved to `C:\Temp\gemini-frames`. The current code no longer does this (the folder is empty), so it could not be used as evidence.
- **Manual verification (to do):**
  1. Start a conversation without turning on screen share. Ask "what do you see on my screen?" and "how many icons are on my desktop?". Gemini must say it can't see the screen.
  2. Click the overlay's screen button. Status should show "Screen share ON: first screenshot sent to Gemini" and Gemini should say it can now see the screen.
  3. Ask about something visible (e.g. a window title or a desktop icon name). The answer should match the real screen.
  4. Turn screen share off and ask again. Gemini must say it can't see the screen.

## Phase 1 Fix - Scrambled / glitchy voice toward the end of Gemini's replies

- **Date:** 2026-09-17
- **Status:** Implemented and verified with the live API; awaiting manual listening test.
- **Reported bug:** Toward the end of Gemini's speech the voice scrambles and distorts with a "technical glitch" sound. It has been seen many times; the earlier fixes (`b73b00e` odd-byte sample alignment, `6944bc6` 20 ms fade tail) addressed clicks, not this.
- **Cause (measured):** Gemini Live sends speech much faster than real time. In a live test, a **38.7 s reply arrived in about 10.6 s** (127 chunks). `AudioPlaybackService` used NAudio `BufferedWaveProvider` with `BufferDuration = 2 s` and `DiscardOnBufferOverflow = true`. That buffer filled 0.8 s into the reply, and every later chunk that didn't fit was silently dropped. **26.0 s of the 38.7 s reply (67%) was discarded.** The remaining fragments were spliced together, which is the scrambled sound. Short replies (under ~2 s of backlog) were unaffected; long replies got progressively worse toward the end.
- **Implementation:**
  - Added `PcmPlaybackQueue`, an unbounded, lossless `IWaveProvider` (queue of PCM segments). It pads with silence only when empty, so the output device never stops. Latency cannot accumulate across turns, because barge-in (`interrupted`) still clears the queue.
  - `AudioPlaybackService` now uses it. The existing odd-byte carry and end-of-turn fade are unchanged.
  - Side effect found and fixed: `SessionOrchestrator` cleared the speaking state 350 ms after the last chunk *arrived*. With lossless playback, the overlay's speaking animation would have stopped ~30 s before long replies finished. Added `IAudioPlaybackService.HasQueuedAudio`; the speaking state now stays on until queued speech has actually played.
- **Also confirmed:** Gemini sent `turnComplete` at 45.1 s for a 44.4 s reply (first chunk at ~0.7 s), so the server tracks real-time playback and user barge-in keeps working for the whole reply.
- **Files changed:** `src/GeminiLiveShare.Core/Audio/PcmPlaybackQueue.cs` (new), `src/GeminiLiveShare.Core/Audio/AudioPlaybackService.cs`, `src/GeminiLiveShare.Core/Audio/IAudioPlaybackService.cs`, `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`, `src/GeminiLiveShare.Tests/Program.cs`.
- **Automated verification:**
  - New `ValidatePlaybackQueueIsLossless` test: 39 s of audio enqueued faster than real time is read back byte-for-byte in order, empty reads pad with silence, and `Clear` empties the queue.
  - Live API: a 44.4 s reply fed into the new queue, **0.0 s lost** (the old buffer lost 67%).
  - Solution builds with 0 errors; the test harness passes.
- **Manual verification (to do):** Ask for a long answer ("tell me a detailed 1-minute story") and listen to the end: no scrambling, skips or glitch sound. The overlay animation should keep moving until the voice actually stops. Interrupt mid-story: the voice should stop promptly.

## Phase 5e Fix - Conversation titles were the first spoken line

- **Date:** 2026-09-17
- **Status:** Implemented and verified with the live API; awaiting manual test.
- **Reported bug:** Conversation titles in the sidebar are the first line the user said, not a proper title like Claude chat generates.
- **Cause (measured):**
  1. `GeminiTitleGenerationService` called `gemini-2.5-flash`, which now returns **HTTP 404 "no longer available to new users, use gemini-3.6-flash"**. The service swallowed the error and returned `null`, so titles were never generated.
  2. The sidebar therefore kept `ChatSessionViewModel.CreateSummary(first user text)`, the first 40 characters of the first transcription fragment.
  3. Even with a working model, the request used `maxOutputTokens = 20`. Thinking models (`gemini-3.6-flash`, `gemini-flash-latest`) spent ~250 tokens thinking and hit `MAX_TOKENS`.
  4. A title was only attempted when a session was stopped from the main view model, once, and never for older conversations.
- **Model selection (measured with this API key):** `gemini-flash-lite-latest`: 0.9–1.2 s, no thinking tokens, good titles. `gemini-3.5-flash-lite`: 0.8 s. `gemini-flash-latest`: 6.9 s, `MAX_TOKENS`. `gemini-3.6-flash`: 3.7 s, `MAX_TOKENS` by default. `gemini-3.1-flash-lite`: intermittent 503. Chose the `-latest` alias as primary (it follows Google's current model, so it won't be retired like a pinned name) with `gemini-3.5-flash-lite` as fallback on 404/429/500/503.
- **Implementation (chat-app style titles):**
  - The sidebar shows **"New conversation"** until a real title exists. The first spoken line is never used (`CreateSummary` removed).
  - **Early title:** as soon as Gemini answers during a live session, a title is generated from the conversation so far. Greeting-only openings return `NONE` and are retried on the next assistant reply (up to 3 attempts).
  - **Final title:** when the conversation stops, the title is regenerated once from the whole transcript. A version counter prevents a slow early request from overwriting the final title.
  - **Backfill:** at startup, up to 20 existing conversations still titled "New conversation" are named in the background.
  - User-renamed titles are never overwritten.
  - Prompt: 2–6 words, Title Case, user's language, main goal across the whole conversation, ignore greetings/mic checks, be specific, no quotes/emoji/punctuation, `NONE` if no topic yet. Voice transcription fragments are merged into speaker turns; long sessions send the first 10 and last 20 turns.
  - Output cleaning strips markdown, `Title:` prefixes, quotes and trailing punctuation, and caps length at 60 characters. Real API errors are now surfaced in the status bar instead of silently falling back.
- **Files changed:** `src/GeminiLiveShare.Core/Gemini/GeminiTitleGenerationService.cs`, `src/GeminiLiveShare.App/ViewModels/MainViewModel.cs`, `src/GeminiLiveShare.App/ViewModels/ChatHistoryViewModels.cs`, `src/GeminiLiveShare.Tests/Program.cs`.
- **Automated verification:**
  - New `ValidateTitleFormatting` test (markdown/prefix/quote cleanup, `NONE` rejected, 60-char cap, fragment merging). It caught and fixed a bug where `**Title: ...**` kept its prefix.
  - Live API results: greeting then trip planning gave "Budget Trip To Jaipur December"; greeting only gave no title (stays "New conversation"); topic shift gave "Junior Data Analyst Cover Letter"; Hindi recipe request gave "Dal Makhani Recipe in Hindi". All took 0.8–1.0 s.
- **Manual verification (to do):**
  1. Launch the app. Older "New conversation" entries should get real titles within a few seconds.
  2. Start a conversation, say "hello, can you hear me", and after Gemini replies confirm the title is still "New conversation".
  3. Ask something substantive. After Gemini answers, the title should change to a short topic title.
  4. Stop the conversation. The title may be refined to reflect the whole conversation.
  5. Rename a conversation manually, then start and stop another. The renamed title must not change.

## Phase 1 Fix - Gemini interrupts itself on speakers (acoustic echo cancellation)

- **Date:** 2026-09-17
- **Status:** Implemented and verified on hardware by automated measurement; awaiting manual conversation test.
- **Reported bug:** With headphones disconnected, Gemini keeps interrupting itself and the user cannot hold a conversation.
- **Cause:** `AudioCaptureService` recorded the raw microphone with NAudio `WaveInEvent` (MME) and applied no echo cancellation. Gemini's voice from the speakers re-entered the microphone and was streamed back to the Live API. Gemini's server-side voice activity detection treated its own voice as user speech, sent `interrupted`, and `SessionOrchestrator.OnInterrupted` cleared playback. This was the "acoustic echo cancellation" item listed under *Phase 6 — Future Hardening* in `docs/ARCHITECTURE.md`.
- **Implementation:**
  - Added `EchoCancellingMicrophone`, a COM interop wrapper around the built-in Windows **Voice Capture DSP** (`CLSID_CWMAudioAEC`), the same echo canceller Windows communication apps use. It runs in source mode (`SINGLE_CHANNEL_AEC`) on a dedicated MTA thread, captures the default console microphone plus a loopback of the default console speaker (the device `WaveOutEvent` plays on), subtracts the speaker signal, and emits 16 kHz / 16-bit / mono PCM in the same 40 ms chunks as before, so the orchestrator's queue tuning is unchanged.
  - `AudioCaptureService` now starts echo-cancelled capture first. If the DSP cannot start, it falls back to the previous raw `WaveInEvent` capture.
  - **Stall watchdog:** the DSP only produces audio while the speaker endpoint is rendering (loopback is its echo reference). If it produces nothing for 3 seconds, capture automatically falls back to raw recording instead of silently sending no audio. During a session this should not trigger, because playback stays open (it renders silence between replies).
  - Unplugging or changing the default device mid-session (50 consecutive DSP failures) raises `CaptureFailed`, which turns the mic off as before; turning the mic back on re-selects the current default devices.
  - Added `IAudioCaptureService.IsEchoCancellationActive`. The session status now shows `Microphone ON (echo cancellation active)` or `Microphone ON (echo cancellation unavailable - use headphones)`.
- **Files changed:** `src/GeminiLiveShare.Core/Audio/EchoCancellingMicrophone.cs` (new), `src/GeminiLiveShare.Core/Audio/AudioCaptureService.cs`, `src/GeminiLiveShare.Core/Audio/IAudioCaptureService.cs`, `src/GeminiLiveShare.Core/Gemini/SessionOrchestrator.cs`, `src/GeminiLiveShare.Tests/Program.cs` (fake updated), `PHASE_LOG.md` (new).
- **Debugging notes:**
  - First smoke test: the DSP initialized but `ProcessOutput` returned `S_FALSE` with no data forever. The cause was that nothing was playing on the speaker. With playback open (as in a real session), it produced exactly 3000 ms of audio per 3 s in 1280-byte chunks. This finding led to the stall watchdog.
  - Plain loudness (RMS) comparisons were dominated by room noise and inconclusive, so echo was measured instead as the least-squares amplitude of the exact played test signal found in the mic recording (best lag 0–500 ms).
- **Automated verification (this PC: Speakers/Microphone "High Definition Audio Device", volume 100%):**
  - Echo amplitude of a 6 s speech-band test signal played through `AudioPlaybackService`, over 3 rounds: raw mic **12.5–15.4**, echo-cancelled mic **2.6–2.9**, no-playback control 0.5–4.0. That is about an 80% (≈14 dB) echo reduction, down to the background-noise level.
  - Start, stop, and restart of echo-cancelled capture works; start time 40–340 ms.
  - Stall fallback: with no playback, capture switched to raw after 3 s and kept delivering audio (62,720 bytes in the remaining 2 s window).
  - `dotnet build GeminiLiveShare.sln`: 0 errors (1 pre-existing test warning). The `GeminiLiveShare.Tests` harness passes.
- **Manual verification (to do):**
  1. Unplug headphones, use laptop/desktop speakers at normal volume.
  2. Start a new conversation. The status should read `Microphone ON (echo cancellation active)`.
  3. Ask a long question ("explain how airplanes fly in detail") and stay silent. Gemini should finish the whole answer without cutting itself off.
  4. Interrupt mid-answer by speaking ("stop, tell me a joke"). Gemini should still stop and switch topic (barge-in must keep working).
  5. Repeat steps 3–4 with headphones to confirm there is no regression.
  6. Mute/unmute the mic mid-session and confirm the status message and capture resume.
- **If interruptions still occur:** a second layer is available but not yet applied. Lower Gemini's server VAD sensitivity (`realtimeInputConfig.automaticActivityDetection.startOfSpeechSensitivity = START_SENSITIVITY_LOW`) in `SetupMessage`. It was held back because it also makes genuine barge-in less sensitive.
















## Phase 7 corrective follow-up — tool stability, search models and highlighting

- **Date:** 2026-09-19
- **Status:** Implemented and automated-tested; real-key/manual acceptance is still pending.
- **Trigger:** The 18:49–19:01 session log showed that Live tool setup reached the app fallback, but regular web search still tried unavailable `gemini-3.0-flash`; tool-call turns also triggered the 4-second audio watchdog, causing disconnect/reconnect cycles. Highlight calls were logged without enough result detail to prove that the overlay was visible or that the right duplicate control was selected.
- **Implementation:**
  - Changed regular `web_search` and `zoom_region` model fallbacks to the lightweight `generateContent` models already verified for this project: `gemini-flash-lite-latest`, then `gemini-3.5-flash-lite`.
  - Paused the assistant-audio watchdog while one or more desktop/search/zoom tools are executing. After tool responses are sent, a separate 15-second watchdog starts; a tool operation is no longer treated as a silent assistant turn.
  - Added non-sensitive tool-response diagnostics for success/error, match count, selected bounds, search source count and zoom bounds. Highlight success now requires the overlay service to report `IsVisible`.
  - Made highlighting search both the foreground window and the UI Automation root, with optional `location` hints (`left_sidebar`, `quick_access`, `desktop`, `taskbar`) used to rank/filter duplicate accessible names. The browser-agent code and extension were not changed.
  - Added an automated regression test that holds a tool call across the old watchdog boundary and verifies there is no reconnect.
- **Files changed:** `src/GeminiLiveShare.Core/Gemini/GeminiWebSearchService.cs`, `GeminiZoomVisionService.cs`, `SessionOrchestrator.cs`, `GeminiLiveClient.cs`, `src/GeminiLiveShare.Core/Desktop/DesktopAutomationService.cs`, `IDesktopAutomationService.cs`, and `src/GeminiLiveShare.Tests/Program.cs`.
- **Automated verification:**
  - `dotnet build GeminiLiveShare.sln --no-restore -p:BaseOutputPath=build-phase7-fix\`: passed, 0 warnings, 0 errors.
  - `dotnet run --project src/GeminiLiveShare.Tests/GeminiLiveShare.Tests.csproj --no-build -p:BaseOutputPath=build-phase7-fix\`: passed, including the new tool-watchdog regression.
  - `git diff --check`: passed.
  - No files under `extension/` or `src/GeminiLiveShare.Core/BrowserAgent/` were modified.
- **Not yet verified:** real API web-search success with the user's key, real zoom response, visible highlight placement at the user's DPI/multi-monitor setup, and long-running manual screen-share stability. Phase 7 remains incomplete until those checks pass.
- **Manual verification:**
  1. Restart the app so the new binary is running. Start a conversation and turn screen sharing on.
  2. Ask for a current web lookup. The log must show `web search tool completed`; if the key has no quota, it must explicitly say search is unavailable rather than inventing an answer.
  3. In File Explorer, ask “highlight Downloads in the left sidebar.” A visible orange box must sit on the sidebar item, not the main content item. Repeat at 125%/150% scaling if available.
  4. Ask for a zoom/read-small-text operation. The log must show a tool response with bounds and no reconnect while the tool is running.
  5. Leave the conversation running for at least two minutes with screen share enabled. There must be no watchdog-triggered `Disconnecting`/`Connected` pair unless the network actually fails.
