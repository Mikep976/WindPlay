# Third-party notices

WindPlay includes or derives from the following open-source software. Package-specific notices are also present in restored NuGet packages.

## AirPlay.Core2

Copyright (c) 2025 natsurainko. MIT License.

The WindPlay protocol project began from AirPlay.Core2 commit `8c746af172eb74a26e74b8ddea1c066042f4d9cb` and contains substantial changes for incremental parsing, per-install cryptographic identity, input bounds, cancellation, ARM64 compatibility, and Windows integration.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## FFmpeg

WindPlay dynamically links the ARM64 builds of `avcodec`, `avutil`, and `swresample` from FFmpegInteropX.Desktop.FFmpeg 8.1.2. FFmpeg is used only for bounded in-memory decoding of AirPlay ALAC and AAC access units; its demuxers, network protocols, encoders, and command-line tools are not shipped or invoked.

- Project: <https://ffmpeg.org/>
- Package: <https://www.nuget.org/packages/FFmpegInteropX.Desktop.FFmpeg/8.1.2>
- Package source commit: `38b88335f99e76ed89ff3c93f877fdefce736c13`
- License: LGPL-2.1-or-later and applicable component licenses. Complete license texts are distributed in `ThirdPartyLicenses` beside WindPlay.

## Bouncy Castle

Cryptographic primitives are provided by Bouncy Castle for C# under the MIT license. See <https://www.bouncycastle.org/about/license/>.

## Makaretu DNS

Multicast DNS and DNS-SD support is provided by Makaretu DNS packages under the MIT license.
