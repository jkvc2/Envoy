# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project overview

Envoy is a LAN chat and file-transfer application. A Windows 11 desktop app (C# WPF + embedded ASP.NET Core) acts as the server, and any device on the same local network can open the chat as a web page. The WPF host is a thin shell — all chat UI lives in the Svelte 5 frontend.

## Build & run

```bash
# Build the C# project
dotnet build

# Run the desktop app (starts the web server at http://<wifi-ip>:53821)
dotnet run

# Frontend dev server (with API proxied to 127.0.0.1:53821)
cd frontend && npm run dev

# Frontend production build → ../wwwroot/
cd frontend && npm run build
```

The `dotnet build` of the C# project automatically copies `wwwroot/` to the output directory (see the `.csproj`). After changing frontend code, run `npm run build` in `frontend/` before testing with `dotnet run`.

## Architecture

```
┌─ WPF Shell ──────────────────────────────────────┐
│  MainWindow.xaml/.cs                              │
│  - Starts WebServer, shows LAN address            │
│  - "Copy address" / "Open chat" buttons           │
└──────┬────────────────────────────────────────────┘
       │
┌──────▼────────────────────────────────────────────┐
│  WebServer.cs (ASP.NET Core, port 53821)          │
│                                                    │
│  Routes:                                           │
│  GET  /api/messages          → message history     │
│  POST /api/messages          → send text           │
│  POST /api/uploads           → start chunked upload│
│  GET  /api/uploads/{id}      → upload status       │
│  PUT  /api/uploads/{id}/chunks/{n} → write chunk   │
│  POST /api/uploads/{id}/complete  → finalize       │
│  GET  /api/files/{id}        → download file       │
│  POST /api/cleanup           → purge expired files │
│  GET  /api/status            → server info         │
│  GET  /api/events            → SSE stream          │
│  /hub                        → SignalR hub         │
│  /ws                         → WebSocket           │
│  /*                          → static files (wwwroot)│
│                                                    │
│  Services (DI singletons):                         │
│  - StorageService  — JSON persistence + files      │
│  - PresenceService — atomic online-count tracker   │
│  - MessageBus     — fan-out to SSE + WebSocket     │
└──────┬────────────────────────────────────────────┘
       │ serves
┌──────▼────────────────────────────────────────────┐
│  wwwroot/ (Svelte 5 + TypeScript, built by Vite)   │
│  App.svelte — single-page chat UI                  │
│  lib/api.ts — REST client + chunked upload logic   │
│  lib/socket.ts — WebSocket reconnecting client     │
```

## Key design decisions

- **Three real-time channels coexist** (`/hub` SignalR, `/ws` WebSocket, `/api/events` SSE). The Svelte frontend uses WebSocket; SSE is a simpler fallback for constrained clients. Messages published via `MessageBus.PublishAsync` fan out to all three.
- **Chunked file uploads** — 4 MB chunks. The server allocates a sparse file at upload creation, writes chunks at their offsets, and moves the completed file into storage after SHA-256 verification.
- **Persistence** — messages are stored as JSON at `%LocalAppData%\Envoy\messages.json`. Uploaded files go to `%LocalAppData%\Envoy\files\`. Upload state is checkpointed per-chunk as JSON so interrupted uploads can resume.
- **Wi-Fi-only binding** — `WebServer.StartAsync` calls `FindWirelessAddress()` which scans for a Wireless80211 adapter with an active gateway. It binds only to that address, not `0.0.0.0`.
- **Sender identity is per-device** — stored in `localStorage` under `envoy-name`. No authentication; this is LAN-only by design.

## File map

| File | Role |
|---|---|
| `Envoy.csproj` | WPF + ASP.NET Core, `net8.0-windows`, copies `wwwroot/` to output |
| `MainWindow.xaml/.cs` | WPF entry point — launches server, shows address |
| `WebServer.cs` | All HTTP routes, SignalR setup, `MessageBus`, Wi-Fi address discovery |
| `StorageService.cs` | Message history, chunked upload handling, SHA-256 verify, cleanup |
| `Models.cs` | `ChatMessage`, `FileAttachment`, `UploadRequest`, `UploadState`, `CreateUploadResponse` |
| `ChatHub.cs` | SignalR `ChatHub` + `PresenceService` (atomic counter) |
| `ChatConverters.cs` | WPF value converters (image/file visibility, file preview URL) |
| `frontend/src/App.svelte` | Full chat UI — message list, file upload, drag-drop, name prompt |
| `frontend/src/lib/api.ts` | Fetch wrapper, `sendText`, chunked `uploadFile` |
| `frontend/src/lib/socket.ts` | WebSocket listener with auto-reconnect |
| `frontend/src/lib/types.ts` | `ChatMessage`, `FileAttachment`, `UploadJob` interfaces |
