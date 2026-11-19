# FM27 Live

Windows FM27 real-time editor with normal desktop mode, F8 overlay, F11 full screen, tray access and signed automatic updates.

[Download the latest release](https://github.com/2dfindomjinx-lang/FM27Live/releases/latest) · [Turkish guide and publishing instructions](README-LIVE.md)

Derived from [Football Manager 27 Editor](https://github.com/Jackhamerp24/Football-Manager-27-Editor). The upstream editor features and build-profile workflow are retained. See [upstream documentation](README-UPSTREAM.md), [LICENSE](LICENSE) and [NOTICE](NOTICE-LIVE.txt).

The Windows compatibility profiles include Steam and Game Pass 27.1.0+2505117. Real-game verification of this distribution remains outstanding. Epic, women's football and exclusive-fullscreen overlays are not claimed supported. Use a backup career for initial testing.

Updates come only from this repository and must carry a valid ECDSA signature from the pinned publisher key. The signing private key is kept outside this repository. Settings persist independently under LocalAppData/FM27Live.

Build and test: `dotnet test Fm27Editor.slnx -c Release`. Package a signed Windows release with `scripts/package-live.ps1` and publish with `scripts/publish-live.py` after review.
