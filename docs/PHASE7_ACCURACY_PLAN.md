# Phase 7 — Accuracy and Reliability

**Status:** Implemented in code; manual verification remains for real Windows UI, DPI/multi-monitor placement, and live quota behavior. Agreed 2026-09-18.
**Read `PROJECT_STATE.md` §5 first** for the diagnosed problems this phase fixes.

## Why this phase comes before the rest of Phase 6

The product's promise is guiding a non-technical user through their computer. Today
the assistant states guesses as facts: it invented taskbar apps that were not on
screen, guessed the mouse pointer's location, and gave a made-up desktop icon count.
Adding more browser write-tools on top of that would automate wrong decisions. The
browser phases 6f–6h also need element finding and highlighting, which 7d and 7g
build.

## The core insight

Gemini Live compresses every video frame to a small fixed size, so fine detail is
gone before the model sees it. Sending bigger frames does not help and actively
harms audio (see `PHASE_LOG.md` §1). The fix is to **stop asking vision to do work
that Windows can answer exactly**, and to give vision a way to look closer when it
genuinely is the only option.

Sources of truth, in order of preference:

1. **UI Automation** — exact element names, types and screen coordinates. Works for
   Windows Settings, Explorer, most apps, and Chromium (which exposes its page tree).
2. **Browser page data** — the existing Phase 6d form scan.
3. **Zoomed vision** — a crop of the full-resolution capture, sent to a regular
   (non-Live) Gemini call. Used only when 1 and 2 cannot reach the content: images,
   PDFs drawn as pictures, games, remote desktop.
4. **Ask the user** — highlight a guess and ask "this one?", or ask them to point.

---

## 7c — System instruction: never guess

**Size:** small. **Do first:** biggest behavior change for the least code.

Extend `GeminiLiveClient.DesktopVisionInstruction`:

- For counting, reading small text, locating the pointer, or identifying icons:
  **call a tool first**. If no tool fits, say the screen is not clear enough. Never
  produce a number, name or position that was not read from a tool result or clearly
  legible in an image.
- When it is unclear what "this" refers to, **ask the user to point the mouse at it**,
  then call `get_element_under_cursor`.
- Include the model name and today's date so the model can answer questions about
  itself and reason about time.

**Done when:** a scripted set of prompts ("how many taskbar icons", "where is my
pointer", "read this small label") produces either a tool call or an honest "I can't
see that clearly" — never an invented number. Verify against the live API, as with
the Phase 2 screen-access work.

## 7d — Exact desktop data through UI Automation

**Size:** medium. Removes the largest class of invented answers.

New Live function tools, backed by the UI Automation code already used in
`CredentialBlurService`:

| Tool | Returns |
|---|---|
| `get_element_under_cursor` | Name, control type, parent path, bounds of the element under the mouse |
| `get_focused_window` | App name, window title, focused element |
| `list_taskbar_items` | Every taskbar button by name, in order |
| `list_desktop_icons` | Every desktop icon by name and grid position |

Notes:

- Return bounds in screen pixels, DPI-aware, with the monitor identified. 7g needs
  these.
- Cap result size; a long list should be summarized rather than flooding the audio
  conversation.
- Never return password-field contents, consistent with the existing privacy rules.

**Done when:** "where is my pointer", "count the taskbar icons" and "find the Codex
icon" are exactly right every time, with nothing inferred from the image.

## 7e — Zoom, for what only vision can see

**Size:** medium.

- `zoom_region(cells | box, question)`: crop from the **full-resolution** capture and
  send the crop to a regular `generateContent` call with high media resolution, then
  return the answer as the tool result. `GeminiTitleGenerationService` already shows
  the non-Live call pattern.
- To let the model say *where* to zoom, either draw a faint labeled grid (A1–D4) on
  the frames sent to Gemini only — never on the user's screen — or ask a regular
  Gemini call for the bounding box of what the user described.
- Send a fresh frame the moment the user starts speaking, instead of waiting for the
  5 s unchanged-frame refresh (`ImageProcessingService.UnchangedFrameRefreshInterval`),
  so answers are never about a stale screen.

**Done when:** it can read small text inside an image or a scanned PDF that UI
Automation cannot reach, and never answers from a frame older than the question.

## 7f — Web search

**Size:** small to medium. **Try the non-code fix first.**

1. Enable billing on the Google Cloud project behind the API key. Restart the app
   (the "no quota" result is cached per app run) and check the log for the
   "Web search is not available" line. If it is gone, this step is complete.
2. If still refused: add a `web_search(query)` tool. Gemini Live calls it; the app
   makes a regular Gemini call with Google Search enabled and returns the result.
3. Either way, surface the state in the UI, not only in a transient status message.

**Done when:** "who makes Antigravity?" returns a real, sourced answer, and the UI
makes it obvious when search is unavailable.

## 7g — Highlight what to click

**Size:** medium to large. This is the feature that makes guidance usable for the
target audience.

`highlight_element(name, role)`:

1. Find the element: UI Automation in the foreground window (visible and enabled);
   browser page data for web pages; a vision bounding box as a last resort.
2. Draw a glowing box around its bounds in a **transparent, click-through overlay**
   (`WS_EX_TRANSPARENT | WS_EX_LAYERED`), so the user clicks the real control.
3. **Hide the overlay from our own screen capture**
   (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`), or Gemini will see and
   describe its own highlight.
4. Clear it when the user clicks, when the screen changes, or after about 8 seconds.

Details that will bite if missed:

- **DPI scaling**: coordinates must be converted correctly at 125% and 150%, or the
  box lands in the wrong place.
- **Multiple monitors**: place the overlay on the monitor containing the element.
- **Off-screen elements**: call `ScrollIntoView` first.
- **Ambiguity**: several matching "Next" buttons means highlight the likeliest and
  ask "this one?", rather than picking silently.
- **Point, do not click.** Clicking for the user risks doing the wrong thing without
  them noticing, and they learn nothing. An opt-in "let Gemini click" setting can
  come later, with confirmation, reusing the 6h confirmation service.

**Done when:** "click Next" puts a box exactly on the right control in Windows
Settings and on a web form, at both 100% and 150% scaling, and the user can click
straight through it.

## 7b — Context across sessions

**Size:** medium. **Demoted** from its original priority: the greeting seen on
Sep 17 turned out to be a deliberately started new conversation, not a failure. But
the Live API does end long sessions on its own, so this is still needed.

- When a fresh session replaces an old one, send the last few turns from chat
  history plus the current screen-share and page-context state.
- Keep this rebuilt state in one place, so reconnects and a future "resume an old
  conversation" feature share it.
- Show "Reconnected" in the UI.

**Done when:** killing the network mid-conversation and reconnecting continues the
topic without a fresh greeting.

## 7a — Diagnostics gaps

**Size:** small. Mostly already done: the existing log answered most questions in
this diagnosis.

- Log the web-search state at every session start, including when it succeeds.
- Add a debug setting that saves each JPEG sent to Gemini (after the privacy filter)
  under `%LOCALAPPDATA%\GeminiLiveShare\`, so a wrong answer can be checked against
  exactly what the model received. Off by default; it writes screen content to disk.

---

## Order of work

1. 7c (small, immediate honesty improvement)
2. 7d (exact answers for the most common questions)
3. 7f (billing check first; code only if needed)
4. 7e (covers what UI Automation cannot reach)
5. 7g (needs 7d's element finding)
6. 7b
7. 7a (as needed alongside the others)

Then resume Phase 6 at 6f, reusing 7d and 7g rather than duplicating them.
