# Dunhill Print Studio

Windows desktop app (.NET 8 + WPF) that drives a Postek ZR300I RFID label printer
over USB or TCP, and two-way syncs with `dunhill-inventory-service` on Vercel.

## What it does

```
┌───────────────────────────────────────────────────────────────┐
│                                                               │
│   Browser (warehouse operator)                                 │
│       │                                                        │
│       │ HTTPS                                                 │
│       ▼                                                        │
│   Vercel API (dunhill-inventory-service)                       │
│       │                                                        │
│       │ HTTPS (5-second poll)                                 │
│       ▼                                                        │
│   Dunhill Print Studio (THIS APP, on the warehouse PC)        │
│       │                                                        │
│       │ USB bulk transfer OR TCP :9100                        │
│       ▼                                                        │
│   ZR300I label printer                                         │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

The Windows app is the **only thing on a website's network that can talk to USB**.
Browsers can't. The Postek "SDK" is a Windows DLL — also can't be used from a website.

## Architecture

| Layer            | Tech                                        |
|------------------|---------------------------------------------|
| UI               | WPF (.NET 8)                                |
| MVVM             | CommunityToolkit.Mvvm                       |
| DI               | Microsoft.Extensions.DependencyInjection    |
| USB              | LibUsbDotNet (WinUSB driver required)       |
| TCP              | raw .NET Socket (port 9100)                 |
| Cloud sync       | HttpClient + BackgroundService              |
| Storage          | JSON files in `%LOCALAPPDATA%\DunhillPrintStudio\` |
| Printer language | PPLZ (ZPL-compatible), see `src/Pplz/PplzBuilder.cs` |

## Build

Requires **.NET 8 SDK on Windows**.

```powershell
cd C:\path\to\dunhill-print-studio

# Debug run (hot reload)
dotnet run

# Single-file release exe (self-contained, no .NET install needed)
dotnet publish -c Release -r win-x64 `
    -p:PublishSingleFile=true `
    -p:SelfContained=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\DunhillPrintStudio.exe`
(≈25 MB, no installer needed, drop on any Windows 10/11 PC)

## USB driver install (Zadig)

The ZR300I ships with a vendor driver that **cannot** be used by libusb.
Before first connection, switch the driver to **WinUSB**:

1. Download Zadig: https://zadig.akeo.ie/
2. Plug in the ZR300I, power on
3. Open Zadig → Options → List All Devices
4. Select "Postek ZR300I" (or unknown USB device with VID 0x0FE6)
5. Choose **WinUSB** as the target driver
6. Click "Replace Driver"
7. Restart the app — printer should now appear in Settings → USB devices

## TCP fallback (no driver install needed)

If the ZR300I is on the same network as the PC:

1. On the printer LCD, set **Interface → Ethernet** and note its IP
2. In Settings → Connection type → TCP, enter the IP and port 9100
3. Click Connect

## File layout

```
src/
├── App.xaml(.cs)             # WPF entry point + DI host
├── MainWindow.xaml(.cs)      # Window shell + TabControl + status bar
├── Pplz/
│   └── PplzBuilder.cs        # Pure PPLZ string builder (no I/O)
├── Usb/
│   ├── PostekUsbTransport.cs # VID 0x0FE6, bulk write to interface 0
│   └── PostekTcpTransport.cs # raw TCP :9100
├── Services/
│   └── PrintService.cs       # Owns the active transport, exposes PrintAsync()
├── Sync/
│   └── CloudSyncService.cs   # Polls /api/jobs/pending every 5s
├── Data/
│   └── JsonStore.cs          # Atomic JSON read/write to %LOCALAPPDATA%
├── Models/
│   └── Models.cs             # InventoryItem, PrintJob, PrinterStatus
├── ViewModels/
│   ├── PrintViewModel.cs
│   ├── SettingsViewModel.cs
│   └── QueueViewModel.cs     # also HistoryViewModel + InventoryViewModel
└── Views/
    ├── PrintView.xaml        # Form + live PPLZ preview + recent jobs
    ├── SettingsView.xaml     # USB/TCP, Zadig warning, cloud URL/token
    ├── InventoryView.xaml
    ├── QueueView.xaml
    └── HistoryView.xaml
```

## Configuration

Edit `appsettings.json` (copied next to the .exe on first run) and re-launch,
or change values live in the Settings tab — they're persisted to
`%LOCALAPPDATA%\DunhillPrintStudio\settings.json`.

## Roadmap

- [x] **Phase 1 (this build)** — desktop UI, USB/TCP transport, PPLZ builder,
      local queue/history, JSON persistence
- [ ] **Phase 2** — cloud poll, push print completions to `/api/jobs/{id}/complete`,
      system-tray mode, autostart on login
- [ ] **Phase 3** — inventory sync, offline mode, multi-user conflict resolution,
      MSI installer (WiX)
- [ ] **Phase 4** — RFID encode feedback (`^RS` status), inlay calibration profiles,
      SmartTagVerify integration

## PPLZ quick reference

```
^XA             start of label
^CI28           UTF-8 encoding
^PWn            print width (dots)
^LLn            label length (dots)
^LHx,y          label home (origin)
^MDn            media darkness (0–30)
^PRn            print speed (in/sec)
^FOx,y          field origin
^A0N,h,w        font A, normal, height h, width w
^FDdata^FS      field data + store
^BYw,r,h        bar default: width, ratio, height
^BCN,h,y,n,m    Code 128: height, print-interpret, UCC, mode
^BQN,m,mag      QR: model, magnification
^RFW,U,b,w,E    RFID: write, UHF, bank, words, EPC format
^FD<epc>^FS     RFID data
^XZ             end of label
```

Labels for ZR300I at 203 dpi: 4"×6" = **812 × 1218 dots**.

## License

Internal — Dunhill Global.
