# Gemini Live Share — Windows App
## Architecture & Phased Build Plan (for building with Claude Code)

---

## 0. What we're building

A Windows desktop app that opens a persistent WebSocket connection to the
**Gemini Live API**, streams the user's **microphone audio** and **screen
(1 FPS)** to it in real time, plays back **Gemini's spoken audio replies**,
and blurs any **passwords / API keys / credentials** out of the screen
*before* a frame ever leaves the device.

Non-goals for the prototype: polished UI, multi-device sync, cloud backend,
team/billing features. Local-first, single binary, single user.

---

## 1. Tech stack (final — no alternatives)

| Layer | Technology | Why |
|---|---|---|
| Language / runtime | **C# / .NET 8** | Best Windows API interop, what the whole stack below assumes |
| UI framework | **WPF** (not WinUI 3) | Mature, huge amount of training data for Claude Code to generate correctly, trivial system-tray + global-hotkey + transparent-borderless-window support. WinUI 3 is newer but MSIX packaging and tray/hotkey interop are still rough — not worth the risk for a solo-built prototype. |
| Audio capture/playback | **NAudio** (WASAPI wrapper) | De facto standard for raw PCM capture/playback in .NET |
| Screen capture | **Windows.Graphics.Capture** (via `CsWin32` interop) | GPU-accelerated, modern, per-window/per-monitor capture |
| OCR (credential detection) | **Windows.Media.Ocr** (built-in WinRT) | Free, GPU-accelerated, no external dependency like Tesseract |
| Password-field detection | **UI Automation** (`System.Windows.Automation`) | Detects `IsPassword` flag on controls — fast (~2–10ms), catches what OCR might blur late |
| Image resize/encode | **SkiaSharp** | Fast downscale + JPEG encode to base64 |
| WebSocket client | **System.Net.WebSockets.ClientWebSocket** | Built-in, no extra dependency, all we need for `wss://` |
| API key storage | **Windows.Security.Credentials.PasswordVault** | Purpose-built secret storage, OS-level encryption, no plaintext files |
| Local chat history | **SQLite** via `sqlite-net-pcl` | Lightweight, embedded, no server |
| MVVM glue | **CommunityToolkit.Mvvm** | Standard, reduces boilerplate, plays well with WPF |
| Global hotkey | **Win32 `RegisterHotKey`** (P/Invoke) | Simplest reliable way to get a global shortcut in WPF |

No backend server. No cloud DB. Everything lives on the user's machine.

**PHASE 3B STATUS: Integrated and hardened.** OCR-based backup credential detection runs alongside Phase 3a on every protected frame, using the separately validated `SoftwareBitmap` OCR path. UI Automation scans visible password controls across the desktop rather than only the foreground window. OCR handles credential labels and values split across adjacent lines and pads matched regions. Both passes run concurrently with independent 500 ms limits; either protection pass failing drops the frame before encoding. The exact sanitized JPEG is saved to `C:\Temp\gemini-frames` before it is submitted to Gemini; save failure also drops the frame. The persisted Sensitive Content Filtering setting controls both passes and defaults to enabled.

---

## 2. High-level architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                         GeminiLiveShare.App (WPF)                │
│   OverlayWindow  |  SettingsWindow  |  TrayIcon  |  ViewModels   │
└───────────────────────────┬───────────────────────────────────--┘
                             │ (data binding / commands only —
                             │  no business logic in the UI layer)
┌───────────────────────────▼───────────────────────────────────--┐
│                       GeminiLiveShare.Core                       │
│                                                                   │
│  ┌───────────────┐   ┌───────────────────┐   ┌────────────────┐ │
│  │ Audio          │   │ Vision              │   │ Gemini         │ │
│  │ - Capture(mic) │   │ - ScreenCapture     │   │ - LiveClient   │ │
│  │ - Playback     │   │ - CredentialBlur    │   │ - SessionMgr   │ │
│  │  (16k in/      │   │ - ImageProcessing   │   │  (wss://, auth,│ │
│  │   24k out PCM) │   │  (blur→downscale→   │   │   reconnect)   │ │
│  │                │   │   encode order!)    │   │                │ │
│  └───────┬────────┘   └─────────┬───────────┘   └───────┬────────┘ │
│          │                      │                       │          │
│          └──────────────┬───────┴───────────┬───────────┘          │
│                          ▼                   ▼                     │
│                 SessionOrchestrator ─── ties audio+video streams   │
│                 to one live Gemini session, independent of each    │
│                 other (no forced sync — this is what keeps voice   │
│                 latency low even if the video pipeline lags)       │
│                                                                     │
│  ┌───────────────┐   ┌───────────────────┐                        │
│  │ Security       │   │ Storage             │                        │
│  │ - ApiKeyVault  │   │ - ChatHistoryRepo   │                        │
│  │ - LocalAuth    │   │   (SQLite)          │                        │
│  └───────────────┘   └───────────────────┘                        │
└─────────────────────────────────────────────────────────────────┘
```

**Golden rule from the earlier design discussion — do not violate it:**
Screen frame processing order is always:
`Capture full-res → detect+blur credentials → THEN downscale → encode → send.`
Never downscale first — text/passwords become unreadable to the blur
detector but may still leak in a legible-enough form.

Audio and video are **independent streams**. Never gate audio on video
processing (OCR, blur, downscale) finishing — that's how the whole "audio
never lags" property is preserved.

---

## 3. Folder structure (full skeleton)

```
GeminiLiveShare/
├── GeminiLiveShare.sln
├── .gitignore
├── README.md
├── docs/
│   └── ARCHITECTURE.md              (this file, copied into the repo)
│
├── src/
│   ├── GeminiLiveShare.App/                  # WPF executable — UI only
│   │   ├── App.xaml
│   │   ├── App.xaml.cs
│   │   ├── app.manifest
│   │   ├── Views/
│   │   │   ├── MainWindow.xaml             # Phase 1 temp debug window
│   │   │   ├── SettingsWindow.xaml         # login + API key entry
│   │   │   └── OverlayWindow.xaml          # Phase 5: floating "dynamic island"
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs
│   │   │   ├── SettingsViewModel.cs
│   │   │   └── OverlayViewModel.cs
│   │   ├── Tray/
│   │   │   └── TrayIconManager.cs
│   │   ├── Resources/
│   │   │   ├── icon.ico
│   │   │   └── Styles.xaml
│   │   └── GeminiLiveShare.App.csproj
│   │
│   ├── GeminiLiveShare.Core/                 # all logic — zero UI references
│   │   ├── Audio/
│   │   │   ├── AudioCaptureService.cs       # NAudio mic → raw PCM 16kHz mono
│   │   │   ├── AudioPlaybackService.cs      # raw PCM 24kHz → speaker
│   │   │   └── IAudioCaptureService.cs / IAudioPlaybackService.cs
│   │   ├── Vision/
│   │   │   ├── ScreenCaptureService.cs      # Windows.Graphics.Capture loop @1fps
│   │   │   ├── CredentialBlurService.cs     # UI Automation + OCR combo
│   │   │   ├── ImageProcessingService.cs    # downscale (720p target) + JPEG + base64
│   │   │   └── IScreenCaptureService.cs / etc.
│   │   ├── Gemini/
│   │   │   ├── GeminiLiveClient.cs          # raw ClientWebSocket wrapper
│   │   │   ├── GeminiSessionManager.cs      # setup msg, reconnect, resumption
│   │   │   ├── SessionOrchestrator.cs       # wires Audio+Vision to one session
│   │   │   └── Models/
│   │   │       ├── SetupMessage.cs
│   │   │       ├── RealtimeInputMessage.cs
│   │   │       └── ServerContentMessage.cs
│   │   ├── Security/
│   │   │   ├── ApiKeyVaultService.cs        # PasswordVault wrapper
│   │   │   └── LocalAuthService.cs          # optional email/password local login
│   │   ├── Storage/
│   │   │   ├── ChatHistoryRepository.cs     # SQLite CRUD
│   │   │   ├── Models/ChatMessage.cs
│   │   │   └── Migrations/
│   │   ├── Interop/
│   │   │   ├── GlobalHotkey.cs              # Win32 RegisterHotKey wrapper
│   │   │   └── WindowsCaptureInterop.cs     # WinRT capture <-> HWND glue
│   │   └── GeminiLiveShare.Core.csproj
│   │
│   └── GeminiLiveShare.Tests/
│       ├── Audio/AudioCaptureServiceTests.cs
│       ├── Vision/CredentialBlurServiceTests.cs
│       ├── Gemini/GeminiSessionManagerTests.cs
│       └── GeminiLiveShare.Tests.csproj
│
└── tools/
    └── sample-frames/                       # test screenshots w/ fake credentials,
                                              # used to validate blur pipeline
```

---

## 4. Phased build plan

Build in this exact order. Each phase ends in something you can actually
run and verify — don't let Claude Code jump ahead to later phases.
**Phase 3 completion = your "working prototype" milestone** (voice + screen
+ privacy all working). Phases 4–5 are persistence and UI polish.

### Phase 0 — Skeleton
- Create the solution + 3 projects (App, Core, Tests) exactly as in the
  folder structure above.
- Wire App → Core project reference. Empty `MainWindow` that just launches.
- Git init, `.gitignore` for .NET/VS.
- **Done when:** solution builds and an empty window opens.

### Phase 1 — Voice core (no video yet)
- `ApiKeyVaultService`: save/read the Gemini API key via `PasswordVault`.
- `SettingsWindow`: one text box to paste the API key, save it.
- `GeminiLiveClient`: open `wss://` connection to the Live API, send the
  setup message (model, generation config — **no video config yet**).
- `AudioCaptureService` (NAudio): mic → 16kHz mono raw PCM → send as
  audio chunks over the socket.
- `AudioPlaybackService`: receive 24kHz PCM from the socket → play on
  speakers immediately (no buffering delay).
- Basic debug `MainWindow`: a "Start/Stop conversation" button + a text log
  of connection state.
- **Done when:** you can click Start, talk into your mic, and hear Gemini
  respond out loud, with the barge-in behavior working (interrupt Gemini
  mid-sentence by talking).

### Phase 2 — Vision core (raw, unblurred — internal testing only)
- `ScreenCaptureService`: `Windows.Graphics.Capture`, loop at 1 FPS,
  produce a full-resolution frame each tick.
- `ImageProcessingService`: downscale to ~1280px width, JPEG @80-85%
  quality, base64 encode.
- Wire into `SessionOrchestrator`: video frames get sent on the same
  socket as an independent stream, never blocking audio.
- ⚠️ **No credential blur yet in this phase** — flag it clearly in the UI
  ("UNSAFE TEST MODE — do not use with real passwords on screen") so you
  never forget this build isn't safe to actually use yet.
- **Done when:** Gemini can describe what's on your screen while you're
  talking to it, with voice latency unaffected by the video pipeline.

### Phase 3 — Privacy layer (this makes it a real prototype)
- `CredentialBlurService`, primary layer: UI Automation — walk the
  foreground window's automation tree, find controls with `IsPassword`,
  black-box their screen region.
- Secondary/backup layer: `Windows.Media.Ocr` on the **full-res** frame,
  regex/pattern match for credit-card numbers, API-key-shaped strings,
  common password-field labels → black-box those regions too.
- Enforce the order strictly in `ImageProcessingService`:
  `full-res capture → blur pass → THEN downscale → encode → send`.
- If OCR ever exceeds your per-frame time budget (you have ~1000ms since
  you're at 1 FPS — OCR typically costs 50-150ms), drop that frame rather
  than queuing and falling behind.
- Test using `tools/sample-frames/` — screenshots with fake passwords/API
  keys visible — confirm they're black-boxed before frames ever reach the
  encode step.
- **Done when:** you can have a real password manager or terminal with a
  real API key open on screen during a session, and it never appears in
  what gets sent. This is your "working prototype ready" milestone.

#### Known limitations

**PHASE 3A LIMITATION:** The desktop-wide UI Automation pass now protects visible password controls in tiled/background windows. During rapid window dragging or shell task-switching transitions, frames are still dropped when control coordinates cannot be associated safely with the captured pixels.

### Phase 4 — Persistence & reliability
- `ChatHistoryRepository` (SQLite): log Gemini's `inputTranscription` /
  `outputTranscription` (you get free transcripts from the Live API
  itself — no separate STT needed).
- `GeminiSessionManager`: reconnect logic on socket drop, session
  resumption so a dropped connection doesn't lose the conversation.
- `LocalAuthService` (optional): simple local email/password gate if you
  want app-level login, purely local, no backend.
- **Done when:** killing your WiFi mid-conversation and restoring it
  resumes gracefully instead of crashing, and past conversations are
  visible in a simple log/list.

### Phase 5 — UI polish (last, as planned)
- `OverlayWindow`: transparent, borderless, always-on-top WPF window.
- `TrayIconManager`: minimize to tray instead of closing.
- `GlobalHotkey`: e.g. `Alt+S` toggles the overlay, expand/collapse
  animation ("dynamic island" feel) via WPF `Storyboard` animations.
- Mic mute / screen-share toggle buttons bound to `SessionOrchestrator`.
- General visual polish, icons, theming.

### Phase 6 — Future Hardening
1. Multi-window blur reliability (see the Phase 3a limitation above).
2. Acoustic echo cancellation for speaker use (headphones work fine, speakers have echo).

---

## 5. Key numbers to remember (from the earlier design discussion)

- Frame rate sent to Gemini: **~1 FPS**, not 60 — the model samples,
  it doesn't need video-call framerates.
- Audio in: **16-bit PCM, 16kHz, mono**. Audio out: **24kHz PCM**.
- End-to-end voice latency target: **<300–500ms** (native audio-to-audio,
  no separate STT/TTS hop needed — the Live API does this natively).
- UI Automation password-field detection: **~2–10ms** (near-free).
- Full-frame OCR pass: **~50–150ms** for a 720p–1080p frame — well inside
  your 1000ms/frame budget at 1 FPS.
- Target downscale width: **~1280px**, JPEG quality **80–85%** → roughly
  80–150KB per frame, trivial bandwidth at 1 FPS.

---

## 6. How to actually use this with Claude Code

Feed this file to Claude Code as the project brief and go phase by phase —
don't ask it to build everything at once. Suggested first prompt:

> "Read gemini-live-share-architecture.md. Set up Phase 0 exactly as
> described — solution, 3 projects, empty MainWindow. Don't write any
> Phase 1+ logic yet."

Then verify each phase actually runs before telling it to move to the next
one. This keeps the build debuggable instead of a 2000-line diff you can't
untangle if something breaks.
