# Gemini Live Share

Windows desktop application built with C#, .NET 8, and WPF.

## Phase 0

This repository currently contains only the solution skeleton:

- `GeminiLiveShare.App` — empty WPF application
- `GeminiLiveShare.Core` — business-logic class library
- `GeminiLiveShare.Tests` — test class library

See `docs/ARCHITECTURE.md` for the phased build plan.

## Phase 6a: browser extension echo test

Phase 6a contains only the MV3 extension skeleton and a standalone native
messaging echo proxy. It does not connect to the WPF application and does not
include a Named Pipe, page scanner, or browser tools.

Build the proxy from the repository root:

```powershell
dotnet build .\src\GeminiLiveShare.NativeMessagingProxy\GeminiLiveShare.NativeMessagingProxy.csproj
```

Then register the host manifest for the current Windows user. First load
`extension` as an unpacked extension at `chrome://extensions`, copy its ID,
and replace `REPLACE_WITH_EXTENSION_ID` in
`native-messaging/com.geminiliveshare.proxy.json`. Update the manifest `path`
if the repository is not at the path shown there.

```powershell
$hostManifest = 'C:\Users\tanis\Desktop\gemini_live\native-messaging\com.geminiliveshare.proxy.json'
$registryPath = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.geminiliveshare.proxy'
New-Item -Path $registryPath -Force | Out-Null
Set-ItemProperty -Path $registryPath -Name '(Default)' -Value $hostManifest
```

For Edge, use the same command with
`HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.geminiliveshare.proxy`.
After registering, open the extension's service-worker console, click the
extension icon once, and confirm that it logs connected, sends a test message,
and receives the identical echoed message. A missing host, invalid path, or
extension ID is reported in that console through the disconnect/error log.
# gemini_live_share_for_windows
