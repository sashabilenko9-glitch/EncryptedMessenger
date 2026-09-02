# EncryptedMessenger
![.NET](https://img.shields.io/badge/.NET%208-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-green)
![EncryptedMessenger](assets/screenshot.png)
A serverless, end-to-end encrypted peer-to-peer messenger for the local network (LAN), built with C# and .NET 8. Two or more instances discover each other automatically and exchange text messages directly — no central server, no internet round-trip.

## Features

- **Serverless P2P messaging** over TCP between instances on the same LAN.
- **End-to-end encryption** using a hybrid scheme: RSA-2048 for the key exchange, AES-256 for message contents.
- **Automatic peer discovery** via UDP broadcast, plus manual contacts by IP address and port.
- **Persistent history** — contacts and messages stored locally with Entity Framework Core + SQLite.
- **Background Windows Service** that keeps receiving messages even when the UI is closed (optional; the app also runs standalone).
- **WPF desktop UI** built with the MVVM pattern (contacts list, chat view with emojis, settings).
- **Delivery / read receipts** and online/offline status.

## Architecture

The solution is split into three projects:

| Project | Type | Responsibility |
|---|---|---|
| `EncryptedMessenger.Core` | Class library | Crypto, networking, IPC, database, models — the shared logic |
| `EncryptedMessenger.WindowsService` | Worker Service | Runs the network stack as a background Windows service |
| `EncryptedMessenger.WPF` | WPF app | Graphical user interface (startup project) |

The UI and the service communicate over a **named pipe** (JSON messages). If no service is running, the WPF app starts the core logic in-process automatically (standalone mode).

```
  WPF UI  <-- named pipe (JSON) -->  Windows Service (Worker)
                                            |
                                    EncryptedMessenger.Core
                                     |                    |
                              TCP (messages)     UDP (peer discovery)
                                     v                    v
                          other instances on the local network
```

## Security model

- Each instance generates an **RSA-2048** key pair on first run; the private key never leaves the machine.
- For every session a random **AES-256** key + IV is generated, encrypted with the peer's public RSA key (**RSA-OAEP with SHA-256**) and sent over.
- Messages are then encrypted with **AES-256-CBC (PKCS7)** using that session key.

> **Note:** This is a study project. It does not currently authenticate peers or protect against active man-in-the-middle attacks (no public-key fingerprint verification). It is intended for confidential messaging inside a trusted local network.

## Tech stack

- **.NET 8** (`net8.0-windows`), **C#**
- **WPF** + **MVVM**
- **Entity Framework Core 8** with **SQLite**
- **Named Pipes** for inter-process communication
- **Worker Service** hosting (`Microsoft.Extensions.Hosting.WindowsServices`)
- **System.Text.Json** for serialization
- Fully **async/await** networking

## Getting started

### Prerequisites

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (or the .NET 8 Desktop Runtime to run only)
- Visual Studio 2022 (recommended)

### Build & run

```bash
git clone https://github.com/sashabilenko9-glitch/EncryptedMessenger.git
cd EncryptedMessenger
dotnet build EncryptedMessenger.slnx
dotnet run --project EncryptedMessenger.WPF
```

Or open `EncryptedMessenger.slnx` in Visual Studio and run the **EncryptedMessenger.WPF** project.

To test on a single machine, start two instances with different ports (see Settings).

### Optional: install the background service

```bash
dotnet publish EncryptedMessenger.WindowsService -c Release
sc create EncryptedMessenger binPath= "C:\path\EncryptedMessenger.Service.exe"
sc start EncryptedMessenger
```

## Configuration & data

All paths are **relative** (`./data/`). On first run the app creates:

- `./data/messenger.db` — the SQLite database (auto-created via EF Core)
- `./data/settings.json` — display name, TCP/UDP ports, discovery toggle
- the private RSA key file

No installation or database server is required — external libraries are restored automatically from NuGet.

## Project structure

```
EncryptedMessenger/
├─ EncryptedMessenger.slnx
├─ EncryptedMessenger.Core/
│  ├─ Encryption/      # RsaCryptoService, AesCryptoService, CryptoManager
│  ├─ Network/         # MessengerServer, MessengerClient, PacketHelper, PeerDiscovery
│  ├─ IPC/             # PipeServer, PipeClient, PipeMessage
│  ├─ Database/        # AppDbContext, ContactRepository, MessageRepository
│  ├─ Models/          # Contact, Message, AppSettings, NetworkPacket
│  └─ Services/        # MessengerService (orchestrator)
├─ EncryptedMessenger.WindowsService/   # Program, MessengerWorker
└─ EncryptedMessenger.WPF/              # App, Views, ViewModels, Converters, Helpers
```

## License

This project is licensed under the MIT License — see the [LICENSE](LICENSE) file for details.
