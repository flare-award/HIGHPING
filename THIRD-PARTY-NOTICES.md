# Third-party notices

## WinDivert 2.2.2

Latency Studio uses the **unmodified official x64 WinDivert 2.2.2** DLL and signed driver from the `WinDivert-2.2.2-A.zip` release. The driver is opened only after the user enables packet handling and approves the Windows UAC request.

- Official binary release: <https://github.com/basil00/WinDivert/releases/tag/v2.2.2>
- Corresponding upstream source: <https://github.com/basil00/Divert/tree/v2.2.2>
- Documentation: <https://reqrypt.org/windivert-doc.html>
- License: upstream offers LGPL-3.0-or-later or GPL-2.0. Latency Studio redistributes the component under the LGPL option. The exact upstream `LICENSE` file is included in each Windows distribution as `ThirdPartyNotices/WinDivert-LICENSE.txt`.
- The complete corresponding source snapshot for commit `1789526ecfb9ff5397c94f9f54c1a3dc2fb60440` is included as `ThirdPartyNotices/WinDivert-2.2.2-Source.zip`, with a source URL and SHA-256 record next to it.
- Verified upstream binary and source archive hashes are pinned in `scripts/Download-WinDivert.ps1`; the build fails on a hash mismatch.

The application source is provided under the MIT License in `LICENSE`. The application does not modify WinDivert or its driver. No WinDivert component is downloaded by the installed application at runtime.

## .NET

The app targets .NET 10 WPF and is published self-contained for `win-x64`; the .NET runtime is part of the Windows package, so no separate .NET installation is required. .NET is distributed under the Microsoft .NET Library License and related component notices. The Windows workflow copies the applicable `LICENSE.txt` and `ThirdPartyNotices.txt` from the installed .NET SDK distribution to `ThirdPartyNotices/DotNet-LICENSE.txt` and `ThirdPartyNotices/DotNet-ThirdPartyNotices.txt`. These accompany the runtime files in both packages.

## Test-only packages

The automated test project uses pinned versions of xUnit, `xunit.runner.visualstudio`, and `Microsoft.NET.Test.Sdk`. These packages are build/test dependencies and are not shipped as application runtime components.
