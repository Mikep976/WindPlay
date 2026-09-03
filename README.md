# WindPlay

WindPlay turns a Windows 11 PC into a private, low-latency AirPlay receiver. It is designed first for native Windows on Arm devices such as the Surface Pro and supports macOS extended-desktop mirroring, ordinary mirroring, and AirPlay audio without a cloud service or telemetry.

> [!IMPORTANT]
> WindPlay is an independent, unofficial implementation. It is not affiliated with or endorsed by Apple Inc. Protected FairPlay/DRM video is intentionally out of scope.

The first test release is under active development. See [docs/TESTING.md](docs/TESTING.md) for the hardware acceptance checklist.

## Privacy defaults

- Local network only; no cloud relay or account.
- A connection PIN is required by default.
- No recording, screenshots, analytics, or crash uploads.
- Diagnostic logging is off by default, stays local, and rotates after seven days. When enabled, logs may include the connecting device name and local-network address.

## Build

Prerequisites: .NET 10 SDK, Visual Studio 2026 with the Windows application development workload, and Windows SDK 10.0.28000 or later.

```powershell
dotnet restore WindPlay.slnx
./eng/build-native-codecs.ps1
dotnet build WindPlay.slnx -c Release -p:Platform=ARM64
```

CI creates an unpackaged, self-contained `win-arm64` ZIP for private testing.

## Acknowledgements

The protocol layer began as a security- and performance-focused fork of natsurainko's MIT-licensed [AirPlay.Core2](https://github.com/natsurainko/AirPlay.Core2). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
