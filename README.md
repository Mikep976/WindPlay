# WindPlay

WindPlay is an experimental Windows 11 AirPlay receiver targeting native Windows on Arm devices such as the Surface Pro. macOS extended desktop, mirroring and audio are implementation goals awaiting hardware acceptance, not verified release claims.

> [!IMPORTANT]
> WindPlay is an independent, unofficial implementation. It is not affiliated with or endorsed by Apple Inc. Protected FairPlay/DRM video is intentionally out of scope.

**Do not install the run-14 artifact.** Its security review identified release-blocking parsers. Remediation is in progress and ARM64 packaging is held by explicit release gates. See [docs/SECURITY-REMEDIATION.md](docs/SECURITY-REMEDIATION.md) for findings, tests and unresolved requirements.

## Privacy defaults

- Local network only; no cloud relay or account.
- A random 100-bit receiver password is required by default; legacy four-digit PINs are migrated.
- No recording, screenshots, analytics, or crash uploads.
- Diagnostic logging is off by default, stays local, and rotates after seven days. When enabled, logs may include the connecting device name and local-network address.

## Build

Prerequisites: .NET 10 SDK, Visual Studio 2026 with the Windows application development workload, and Windows SDK 10.0.28000 or later.

```powershell
dotnet restore WindPlay.slnx
./eng/build-native-codecs.ps1
dotnet build WindPlay.slnx -c Release -p:Platform=ARM64
```

Push CI runs security tests only. The manual ARM64 workflow fails before building while any gate in `eng/release-gates.json` is unresolved. Do not bypass those gates to obtain a test ZIP.

## Acknowledgements

The protocol layer began as a security- and performance-focused fork of natsurainko's MIT-licensed [AirPlay.Core2](https://github.com/natsurainko/AirPlay.Core2). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
