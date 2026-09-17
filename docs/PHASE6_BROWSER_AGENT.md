# Phase 6 — Browser Agent Integration
## Full Architecture & Phased Build Plan (Verified)

---

## 0. Goal

Let Gemini (already talking to the user via GeminiLiveShare) see structured
form data on the user's active browser tab and fill fields in on the
user's request — without opening a separate automation browser, without
losing the user's existing login session, and without ever letting the
model execute arbitrary code in the page.

This is a big feature. It touches three separate runtimes (browser
extension, a native proxy process, and the existing WPF app) plus a new
tool-calling surface inside `SessionOrchestrator`. It is broken into 10
small, independently-testable phases (6a–6j) instead of the original 6.
**Do not build two phases in one sitting.** Each phase has its own
"Done when" checklist — don't move on until you've manually verified it.

---

## 1. Architecture Decision — verified against 3 alternatives

| Option | How it works | Verdict |
|---|---|---|
| **A. Browser extension + Native Messaging** | Extension talks to a native host process via Chrome's official stdio-based protocol | ✅ **Chosen** |
| **B. Local WebSocket server in the WPF app** | Extension connects to `ws://127.0.0.1:PORT` like a regular WebSocket | ❌ Rejected |
| **C. Own controlled browser (Playwright/PuppeteerSharp)** | App launches/attaches to a browser it drives directly, no extension | ❌ Rejected |

### Why B (local WebSocket) was rejected

A loopback WebSocket server is a port any local process can attempt to
connect to — there's no browser-enforced identity check on who's allowed
to talk to it, so you'd have to build your own token/handshake scheme
from scratch. Native Messaging gets this for free: **Chrome itself
enforces an extension-ID allowlist** in the host manifest, so only your
specific extension can ever reach the host process. For an app built
around "don't leak sensitive data," that's the deciding factor.

### Why C (own controlled browser) was rejected

A Playwright/PuppeteerSharp-driven browser is either a **brand-new
browser profile** (user has to log into every site again — unacceptable
friction) or requires launching the user's real Chrome with a
`--remote-debugging-port` flag (their normal daily-use Chrome can't just
be opened normally anymore — also bad UX). Native Messaging lets the
user keep using their actual, already-logged-in Chrome/Edge with zero
change to how they normally browse.

### Why A (Native Messaging) is correct — verified against real products

Password managers have the *exact* same "browser-extension-needs-to-
talk-to-a-trusted-desktop-app" problem, with even higher security
stakes:

- **Bitwarden**: uses Native Messaging — a "lightweight proxy
  application baked into our desktop binary." As of a June 2026
  release, Bitwarden made Native Messaging permission **mandatory by
  default** — an actively maintained, load-bearing pattern for a major
  security product right now, not a legacy approach.
- **1Password**: also uses Native Messaging (`NativeMessagingUserLevelHosts`,
  `NativeHostsExecutablesLaunchDirectly` policies).
- **KeePassXC**: same pattern.

### The "dumb proxy" detail (easy to miss)

Chrome's Native Messaging spec launches the **native host as a new
subprocess for every connection**. If `GeminiLiveShare.App` itself were
registered directly as the native host, every browser connection would
try to spawn *another* instance of our WPF app — wrong, since we already
run as a single persistent instance (tray icon, live Gemini session).

Bitwarden solves this with a **"dumb proxy"**: the thing registered with
Chrome as the native host is a tiny, separate, lightweight executable
that does nothing except immediately connect to the *already-running*
main app via local IPC and relay bytes back and forth.

```
Chrome/Edge  <--stdio-->  NativeMessagingProxy.exe  <--Named Pipe-->  GeminiLiveShare.App (already running)
             (spawned per connection,                (persistent connection to
              short-lived, ~zero logic)                the one real app instance)
```

Named Pipes (not a TCP socket) are used for the proxy-to-app hop — a
Windows-native, loopback-only IPC mechanism with OS-level access
control, keeping "no open network port" true end-to-end.

---

## 2. Final Architecture

```
┌───────────────────────────────────────────────────────────────┐
│                  Browser (Chrome / Edge)                       │
│  ┌────────────────────────────────────────────────────────┐   │
│  │  Extension (Manifest V3)                                │   │
│  │  - Service worker: owns the Native Messaging port,      │   │
│  │    dispatches tool calls, tracks per-tab activation      │   │
│  │  - Content script: injected ON-DEMAND ONLY via           │   │
│  │    chrome.scripting.executeScript + "activeTab" perm     │   │
│  └───────────────────────┬────────────────────────────────┘   │
└──────────────────────────┼──────────────────────────────────--┘
                            │ Native Messaging (stdio, length-prefixed JSON)
┌───────────────────────────▼──────────────────────────────────--┐
│         NativeMessagingProxy.exe  (tiny, spawned per connection)│
└───────────────────────────┬──────────────────────────────────--┘
                            │ Named Pipe (persistent, local-only)
┌───────────────────────────▼──────────────────────────────────--┐
│                  GeminiLiveShare.App (existing instance)        │
│  ┌────────────────────────────────────────────────────────┐    │
│  │  NEW: BrowserAgentBridge                                 │    │
│  │  - Receives page/field data from the extension            │    │
│  │  - Exposes a fixed TOOL REGISTRY to Gemini                │    │
│  │  - Enforces confirmation policy before any write action   │    │
│  └───────────────────────┬────────────────────────────────┘    │
│                           │                                     │
│                  SessionOrchestrator (existing)                 │
└──────────────────────────────────────────────────────────────--┘
```

### New folders/projects added to the existing solution

```
GeminiLiveShare/
├── src/
│   ├── GeminiLiveShare.App/
│   │   ├── Views/
│   │   │   └── BrowserAgentDebugPanel.xaml      # 6b, temporary
│   │   └── ViewModels/
│   │       └── BrowserAgentViewModel.cs
│   │
│   ├── GeminiLiveShare.Core/
│   │   └── BrowserAgent/
│   │       ├── BrowserAgentBridge.cs             # owns the named pipe server
│   │       ├── BrowserAgentToolRegistry.cs       # tool definitions + dispatch
│   │       ├── BrowserConfirmationService.cs     # confirmation-gated actions
│   │       ├── AllowedDomainStore.cs             # 6i
│   │       └── Models/
│   │           ├── PageSnapshot.cs
│   │           ├── FormField.cs
│   │           ├── ToolCallRequest.cs
│   │           └── ToolCallResult.cs
│   │
│   ├── GeminiLiveShare.NativeMessagingProxy/     # NEW standalone project
│   │   ├── Program.cs
│   │   ├── StdioFramer.cs                        # raw Stream framing (see §6)
│   │   ├── NamedPipeRelay.cs
│   │   └── GeminiLiveShare.NativeMessagingProxy.csproj
│   │
│   └── GeminiLiveShare.Tests/
│       └── BrowserAgent/
│           ├── StdioFramerTests.cs
│           ├── BrowserAgentBridgeTests.cs
│           └── ToolRegistryTests.cs
│
├── extension/                                    # NEW, separate from .NET solution
│   ├── manifest.json
│   ├── background/
│   │   └── service-worker.js
│   ├── content/
│   │   └── field-scanner.js
│   ├── content/
│   │   └── highlight.css
│   └── icons/
│
└── native-messaging/
    └── com.geminiliveshare.proxy.json            # host manifest, registered in registry
```

---

## 3. Tool Registry (the ONLY things Gemini can ask the browser to do)

| Tool | Effect | Confirmation required? |
|---|---|---|
| `get_active_page` | Returns URL + title of the active tab | No (read-only) |
| `get_form_fields` | Returns structured field list | No (read-only) |
| `fill_field` | Sets one field's value | No (highlight only) |
| `select_option` | Picks a dropdown option | No (highlight only) |
| `clear_field` | Clears one field | No (highlight only) |
| `focus_field` | Scrolls to / focuses a field | No |
| `click_button` | Clicks a non-submit button | **Yes** |
| `submit_form` | Submits the form | **Yes, always** |

### Field data shape (sent extension → app, NOT full HTML)

```json
{
  "url": "https://example.com/apply",
  "title": "Application Form",
  "fields": [
    {
      "id": "field_12",
      "label": "First name",
      "type": "text",
      "required": true,
      "value": ""
    }
  ]
}
```

Structured metadata instead of raw HTML: smaller payload, faster for
Gemini to reason over, and it naturally excludes anything not recognized
as a real form field.

### Native Messaging wire protocol (extension ⇄ proxy ⇄ app)

Every message, in both directions, is a single JSON envelope:

```json
{
  "type": "tool_call | tool_result | event | handshake",
  "requestId": "uuid",
  "payload": { }
}
```

- `handshake` — sent once when the extension's service worker first
  connects; carries extension version + tab id.
- `tool_call` — app → extension, e.g. `{"tool":"fill_field","args":{...}}`.
- `tool_result` — extension → app, success/failure + data.
- `event` — extension → app, unsolicited (tab closed, navigation
  happened mid-operation, user revoked domain permission).

`requestId` lets `BrowserAgentBridge` match async tool_call → tool_result
pairs even if multiple calls are in flight (e.g. `get_active_page` and
`get_form_fields` fired back-to-back).

---

## 4. Security & Privacy Rules (non-negotiable, apply across all phases)

1. **No arbitrary code execution.** Gemini only ever calls the fixed
   tool list. The content script only implements those specific
   operations — never a generic "run this JS" endpoint.
2. **On-demand injection only**, via `activeTab` + `chrome.scripting`.
   Requires an explicit user action before any script runs on a page —
   no always-on content script. No Chrome Web Store permission warning
   at install time either, since `activeTab` doesn't trigger one.
3. **Password fields excluded by default.** `fill_field`/`get_form_fields`
   never reads or writes `type="password"` inputs. Consistent with the
   Phase 3 credential-blur philosophy.
4. **Never auto-submit.** `submit_form`/`click_button` always require
   explicit user confirmation, in v1 without exception.
5. **Visible "GeminiLiveShare is monitoring this page" indicator**
   whenever the extension is active on a tab.
6. **Allowed-domain list**, off by default, configurable in Settings.
7. **Field-level visual feedback** — a CSS outline on every write, so
   the user sees exactly what changed in real time.

---

## 5. Phased Build Plan (6a → 6j)

Each phase below lists: **Scope**, **What gets built (file by file)**,
**Data/contract touched**, **Manual test steps**, and **Done when**.
Do not start a phase until the previous one's "Done when" is checked off.

---

### Phase 6a — Extension skeleton + proxy process, no relay yet

**Scope:** Get a Manifest V3 extension installed and a standalone proxy
executable that Chrome can successfully launch. No app connection yet —
just prove Chrome will talk to our proxy at all.

**Build:**
- `extension/manifest.json` — MV3, `permissions: ["nativeMessaging", "activeTab", "scripting"]`, background service worker registered.
- `extension/background/service-worker.js` — on extension icon click, call `chrome.runtime.connectNative("com.geminiliveshare.proxy")`, log connect/disconnect/error to the extension's own console.
- `GeminiLiveShare.NativeMessagingProxy/Program.cs` — bare entry point: on launch, read one length-prefixed message from stdin, echo it back unmodified, exit. No named pipe yet.
- `native-messaging/com.geminiliveshare.proxy.json` — host manifest with `allowed_origins` locked to your extension's ID (get this after first unpacked-load), path pointing at the built proxy `.exe`.
- A one-time install script/README step: write the host manifest path into `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.geminiliveshare.proxy` (and the Edge equivalent key).

**Test steps:**
1. Load the unpacked extension in Chrome (`chrome://extensions`, Developer mode).
2. Copy its extension ID into the host manifest's `allowed_origins`.
3. Register the registry key, click the extension icon.
4. Confirm the extension's console shows "connected" and an echoed test message comes back.

**Done when:** a message sent from the service worker round-trips through the proxy and back, visible in the extension's console. No app involvement yet.

---

### Phase 6b — Named Pipe relay to the running app

**Scope:** Make the proxy stop echoing and instead forward bytes to the already-running `GeminiLiveShare.App` over a Named Pipe, and have the app log that a browser connected.

**Build:**
- `GeminiLiveShare.NativeMessagingProxy/NamedPipeRelay.cs` — connects to a well-known pipe name (e.g. `\\.\pipe\GeminiLiveShare.BrowserAgent`) on startup; if the app isn't running / pipe doesn't exist, fail fast and write an `event` back to the extension so it can show "app not running."
- `GeminiLiveShare.Core/BrowserAgent/BrowserAgentBridge.cs` — `NamedPipeServerStream` listener, started at app launch (alongside the existing tray/session init), accepts one proxy connection at a time, logs `handshake` events to the existing session log.
- Wire `StdioFramer` (raw `Stream`, see §6) into the proxy so it isn't just line-based stdin/stdout anymore.

**Test steps:**
1. Launch `GeminiLiveShare.App` normally (tray icon appears).
2. Click the extension icon in Chrome.
3. Confirm the app's session log shows "Extension connected" (this is the "hello world" milestone from the original plan).
4. Kill the app, click the extension icon again — confirm the extension surfaces a clear "not connected" state instead of hanging silently.

**Done when:** extension → proxy → Named Pipe → app handshake succeeds and is visible in the app's own log, and the failure path (app not running) is handled gracefully, not silently.

---

### Phase 6c — `get_active_page` end-to-end (simplest possible tool)

**Scope:** Implement the smallest, lowest-risk tool first to validate the full round-trip tool-call shape before building anything that touches the DOM.

**Build:**
- `GeminiLiveShare.Core/BrowserAgent/BrowserAgentToolRegistry.cs` — registry skeleton: a dictionary of tool name → handler, starting with just `get_active_page`.
- `extension/background/service-worker.js` — handle incoming `tool_call` messages, route `get_active_page` to `chrome.tabs.query({active:true, currentWindow:true})`, reply with a `tool_result`.
- `GeminiLiveShare.App/Views/BrowserAgentDebugPanel.xaml` (temporary debug UI) — a button "Get active page" that fires the tool call through `BrowserAgentBridge` and displays the raw JSON result.

**Test steps:**
1. With the extension connected, click the debug panel's "Get active page" button while on a few different real tabs.
2. Confirm URL + title returned match what's actually open, including edge cases: `chrome://` internal pages (should return a clear "not accessible" result, not crash), a tab with no title yet (still loading).

**Done when:** the debug panel reliably shows correct page info for normal pages and fails gracefully (no crash, no hang) on restricted pages.

---

### Phase 6d — `get_form_fields` (DOM scanning, still read-only)

**Scope:** The first tool that actually injects a content script and reads the DOM. This is the highest-complexity read-only tool — get it right before any write tool exists.

**Build:**
- `extension/content/field-scanner.js` — injected on-demand via `chrome.scripting.executeScript` (never persistent). Walks `document.forms` and standalone labelable elements, builds the `FormField[]` shape from §3, explicitly skips `input[type=password]`, and generates a stable-for-this-injection `id` per field (e.g. a data attribute stamped onto the element so later `fill_field` calls in the same session can re-target it reliably).
- `GeminiLiveShare.Core/BrowserAgent/Models/FormField.cs`, `PageSnapshot.cs` — C# mirror of the JSON shape.
- Extend `BrowserAgentToolRegistry` with `get_form_fields`.
- Extend the debug panel to render the returned fields as a simple list.

**Test steps:**
1. Run against a handful of real, varied test forms: a simple contact form, a multi-section signup form, a form with a password field (confirm it's excluded), a form inside an `<iframe>` (confirm it's either handled or explicitly reported as unsupported, not silently dropped).
2. Confirm required-field detection (`required` attribute, `aria-required`) is accurate.
3. Confirm field labels resolve correctly for `<label for>`, wrapped labels, and `aria-label` fallback.

**Done when:** field data arrives correctly in the debug panel for all test forms in `tools/sample-forms/` (new folder, mirrors the existing `tools/sample-frames/` pattern from Phase 3), passwords are never included, and iframe/edge cases fail explicitly rather than silently.

---

### Phase 6e — Wire into the Gemini conversation as context (still no writes)

**Scope:** Gemini can now *see and talk about* the form, using the exact same "give it as context" pattern already used for screen-share frames — no new conceptual pattern needed.

**Build:**
- `SessionOrchestrator` — add a `BrowserAgentBridge` reference alongside `Audio`/`Vision`; on a new page snapshot (fetched on explicit user trigger, e.g. voice command "look at this page" or extension-icon click, *not* polled continuously), inject it into the live session context the same way a screen frame is injected.
- Remove/hide the temporary debug panel's manual trigger button in favor of the real voice-triggered path (keep the panel for dev builds behind a debug flag).
- Prompt/tool-schema update so Gemini knows `get_active_page`/`get_form_fields` exist and when it's appropriate to call them (only after the user asks about the page or a form on it).

**Test steps:**
1. Start a live session, open a real form, say "look at this page and tell me what fields are on it."
2. Confirm Gemini's spoken response accurately describes the fields without ever mentioning field values it wasn't given (i.e., it's not hallucinating field contents).
3. Confirm nothing is sent to Gemini until the user explicitly triggers it — verify via the session log that no `get_form_fields` call fires on its own.

**Done when:** Gemini can verbally and accurately describe a real form's fields, only after explicit user request, with zero write actions available yet.

---

### Phase 6f — Single-field writes: `fill_field`, `clear_field`, `focus_field`

**Scope:** The first write tools. Deliberately excludes `select_option` (dropdowns are more complex DOM interaction, see 6g) and excludes anything confirmation-gated (see 6h).

**Build:**
- `extension/content/field-scanner.js` — extend with write handlers: locate the previously-stamped field id, set `.value`, dispatch synthetic `input`/`change` events (required for frameworks like React-controlled forms to pick up the change — a common gotcha), then focus/scroll into view for `focus_field`.
- `extension/content/highlight.css` — a short-lived CSS outline/glow class applied on write, auto-removed after ~1.5s via a timeout in the content script.
- Extend `BrowserAgentToolRegistry` with `fill_field`, `clear_field`, `focus_field`.
- `BrowserAgentBridge` — no confirmation gate needed for these three (per §4), but every call still logged to session history for later "what did it change" auditability.

**Test steps:**
1. Ask Gemini to fill specific fields by voice on real, low-stakes test forms (a scratch Google Form, a local test HTML form) — confirm values land correctly and the highlight is visible.
2. Test on a React-controlled form (e.g. a modern SPA) — confirm the synthetic event dispatch actually registers with the framework's state, not just the raw DOM value.
3. Test `clear_field` and `focus_field` independently.
4. Confirm password fields still can't be targeted even if a field id is guessed/replayed.

**Done when:** all three tools reliably work on both plain HTML forms and at least one JS-framework-driven form, with visible highlight feedback and no password-field leakage.

---

### Phase 6g — `select_option` (dropdowns)

**Scope:** Split out from 6f because native `<select>`, custom ARIA comboboxes, and framework-driven "fake dropdowns" (div-based, e.g. React-Select-style widgets) all need different handling — worth its own testable unit.

**Build:**
- `extension/content/field-scanner.js` — extend field detection to classify dropdown-like elements distinctly (native `<select>` vs `role="combobox"`/`role="listbox"` patterns), and extend the field JSON schema with an `options: string[]` array for these fields.
- `select_option` handler: for native `<select>`, set `.value` + dispatch `change`; for ARIA comboboxes, simulate the click-open → click-option sequence since there's no single DOM property to set.
- Extend `BrowserAgentToolRegistry` with `select_option`.

**Test steps:**
1. Test against a native `<select>` form.
2. Test against at least one common custom-dropdown pattern (e.g. a Material UI or React-Select demo page).
3. Confirm graceful failure (clear error back to Gemini, not a silent no-op) on dropdown patterns not yet supported.

**Done when:** native selects work reliably; at least one custom-combobox pattern works; unsupported patterns fail with a clear, reportable error rather than doing nothing silently.

---

### Phase 6h — Confirmation-gated actions: `click_button`, `submit_form`

**Scope:** The two highest-consequence tools. This phase is as much about the confirmation UX as the browser mechanics.

**Build:**
- `GeminiLiveShare.Core/BrowserAgent/BrowserConfirmationService.cs` — when Gemini requests `click_button` or `submit_form`, the tool call is *not* immediately forwarded to the extension. Instead it's queued, a confirmation UI is shown (overlay or dialog, reusing the existing `OverlayWindow`/tray notification patterns from Phase 5 where possible), and only forwarded to the extension after explicit user approval (click or a distinct spoken "yes, submit it" — not just any affirmative-sounding speech, to avoid accidental triggers from ambient conversation).
- Extend `extension/content/field-scanner.js` with `click_button`/`submit_form` handlers — button click by stamped id; form submit via the form's own `requestSubmit()` (not a raw click on a submit button, so it respects native validation).
- Session log entry for every confirmation shown, approved, or declined/timed-out.

**Test steps:**
1. Ask Gemini to submit a real low-stakes test form; confirm the confirmation UI blocks the action until explicit approval.
2. Decline the confirmation — confirm nothing happens and Gemini is told the user declined (so it doesn't silently retry).
3. Test the timeout path (no response) — confirm it fails safe (does not submit) rather than defaulting to yes.
4. Specifically test that ambient conversation containing the word "yes" for an unrelated reason does *not* trigger approval — confirmation must be tied to a distinct UI action or an unambiguous, scoped voice confirmation flow, not general sentiment.

**Done when:** no click or submit ever happens without an explicit, unambiguous user confirmation, decline and timeout both fail safe, and all three outcomes are logged.

---

### Phase 6i — Allowed-domain list + monitoring indicator (Settings)

**Scope:** The persistent trust boundary — the extension should do nothing at all on domains the user hasn't approved, and it should always be visually obvious when it's active.

**Build:**
- `GeminiLiveShare.Core/BrowserAgent/AllowedDomainStore.cs` — persisted list (reuse the existing SQLite `Storage` layer from Phase 4 rather than inventing a new store), default empty (off by default per §4).
- `SettingsWindow` — new section: add/remove domains, toggle "ask every time" vs "remember this domain."
- `extension/background/service-worker.js` — before any content-script injection, check the current tab's domain against the allowed list (synced from the app on connect/update); if not allowed, show a lightweight in-extension prompt to add it rather than silently failing.
- A persistent, unmissable on-page indicator (small injected badge, not just a browser-action icon change, since the icon can be easy to miss) rendered whenever the content script is active on a tab.

**Test steps:**
1. Confirm the extension does nothing on a fresh install until at least one domain is approved.
2. Add a domain, confirm tools work there; confirm they still don't work on a different, non-approved domain.
3. Remove a domain mid-session, confirm any subsequent tool call on that domain is blocked, not just new sessions.
4. Confirm the on-page indicator appears/disappears correctly as the content script activates/deactivates.

**Done when:** the domain allowlist is enforced on every tool call (not just at injection time), persists across app restarts, and the monitoring indicator is always accurate and hard to miss.

---

### Phase 6j — Hardening pass

**Scope:** Everything that only shows up under real-world flakiness — this is the phase that makes it trustworthy enough for daily use, not just demoable.

**Build/verify:**
- Tab closed mid-operation (between `get_form_fields` and a `fill_field` call) — confirm a clear "tab no longer available" error surfaces to Gemini/user instead of a hang.
- Navigation away from the page mid-operation — same, plus confirm stale field ids from the old page are never silently applied to the new page.
- Native Messaging port drop (proxy crash, Chrome restart) — `BrowserAgentBridge` detects the pipe disconnect and surfaces a reconnect-needed state; extension attempts reconnect on next user action rather than requiring a manual extension reload.
- App restart while extension is connected — confirm the extension detects the dead pipe and re-handshakes cleanly once the app is back up (builds on the 6b failure path).
- Full regression pass across `tools/sample-forms/`: simple, multi-step/multi-page, dropdown-heavy, and at least one real-world third-party site (with permission/allowlisting) to catch anything the synthetic test forms didn't.
- Written test log added to the repo (mirrors how Phase 3's `tools/sample-frames/` verification was documented) so regressions are catchable later.

**Done when:** every failure mode above has been manually triggered at least once and confirmed to fail safely (never a hang, never a stale/wrong-page write, never a silent no-op that looks like success), and the sample-forms regression pass is clean.

---

## 6. .NET Implementation Gotchas (verified — these cause real, hard-to-diagnose bugs if missed)

1. **Use raw `Stream`, not `StreamReader`/`StreamWriter`, for stdin/stdout in the proxy.** A known, documented C#/.NET bug: text-mode readers on the Native Messaging stdio streams corrupt the binary length-prefix framing (messages arrive with fewer bytes than their length prefix claimed). Use `Console.OpenStandardInput()`/`OpenStandardOutput()` as raw byte streams.
2. **4-byte length prefix, native (little-endian on Windows) byte order, followed by UTF-8 JSON** — same framing in both directions.
3. **Never write logs to stdout in the proxy process** — stdout is exclusively the message-framing channel; a stray `Console.WriteLine` debug log will corrupt the protocol. Log to a file or stderr only.
4. **Message size limits are real and enforced by Chrome**: host→browser capped at 1MB, browser→host much larger — our JSON payloads (field lists, not full HTML) should stay tiny regardless.
5. **Synthetic DOM events matter for framework-driven forms** (see 6f) — setting `.value` alone is often invisible to React/Vue's internal state; dispatch proper `input`/`change` events too.
6. **An existing, tested OSS library** (`Lyre`, MIT-licensed, targets .NET/.NET Core) already implements the Native Messaging framing protocol correctly, with cancellation-token support and proper disposal. Reuse it for the proxy's fiddly protocol-framing part rather than hand-rolling it, and spend the actual engineering effort on the tool registry and safety logic instead.

---

## 7. What NOT to build in v1

- No multi-step form "wizard" navigation logic yet (future work, beyond 6j's basic multi-step test coverage).
- No Firefox/Safari support yet — Chrome/Edge (both Chromium, same extension APIs) only.
- No auto-detection of "this looks like a form, offer to fill it" proactive behavior — v1 is user-initiated only.
- No arbitrary JS execution capability, ever, at any phase — this is a permanent constraint, not a v1 limitation to relax later.

---

## 8. Suggested Claude Code prompting

Same discipline as the main app build: feed this file in, go phase by
phase, and don't let it jump ahead.

> "Read phase6-browser-agent-architecture.md. Build Phase 6a exactly as
> described — extension skeleton + standalone echo proxy. Don't write
> any 6b+ logic (no named pipe, no app-side bridge) yet."

Verify each phase's "Done when" checklist yourself before requesting the
next one.
