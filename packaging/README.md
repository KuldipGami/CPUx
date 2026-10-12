# Packaging CPUx

One script builds every release file:

```
powershell -ExecutionPolicy Bypass -File packaging\build.ps1
```

| Output (in `artifacts\`) | What it is | Who it's for |
| --- | --- | --- |
| `installer\CPUx-Setup-<version>-x64.msi` | Windows Installer with .NET included. Installs to Program Files and adds Start menu and desktop shortcuts plus an entry in Settings › Apps. | Installing on any 64-bit Windows 10/11 PC |
| `store\CPUx_<version>.0_x64.msix` | Microsoft Store package | Uploading to Partner Center |
| `portable\CPUx.exe` | Single 6.5 MB file, no install | PCs that already have the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

`-Only Msi`, `-Only Msix` or `-Only Portable` builds just one. Only the .NET 8 SDK is needed: WiX Toolset 5 (installer) and Microsoft's Windows SDK build tools (MSIX) are fetched from NuGet on first use. The version comes from `<Version>` in `src/CPUx/CPUx.csproj`; raise it for every release.

Other files here:

| Path | Purpose |
| --- | --- |
| `msi/Package.wxs` | Installer definition (WiX). Keep the `UpgradeCode`; it lets new versions replace old ones. |
| `msix/AppxManifest.template.xml` | Store package manifest |
| `msix/Assets/` | App icons and tiles in every size Windows uses |
| `make-assets.ps1` | Regenerates those icons, the installer artwork and the Store logos from the CPUx logo |
| `store/identity.json` | Your Store identity (fill in once, see below) |
| `store/listing.md` | Ready-to-paste Store description, features, search terms and screenshot captions |
| `../docs/privacy.md` | Privacy policy, published with GitHub Pages at https://kuldipgami.github.io/CPUx/privacy/ |
| `store/screenshots/` | 1920 × 1080 screenshots for the listing |
| `store/images/` | Optional Store logos (300 × 300 and box art) |

## Installing with the MSI

Double-click the `.msi`. Windows asks for administrator permission because CPUx installs to Program Files. To update, run a newer installer; it replaces the old version. To uninstall, go to Settings › Apps › Installed apps › CPUx.

Silent install for IT use: `msiexec /i CPUx-Setup-1.1.0-x64.msi /qn` (add `INSTALLDESKTOPSHORTCUT=0` to skip the desktop shortcut).

The installer isn't code-signed, so when you share it, Windows SmartScreen may show "Windows protected your PC". Click **More info → Run anyway**. A code-signing certificate, or Microsoft's Azure-based code-signing service, removes the warning. You don't need one for the Store, because Microsoft signs Store packages itself.

## Publishing to the Microsoft Store

1. **Create a developer account** at [partner.microsoft.com/dashboard/registration](https://partner.microsoft.com/dashboard/registration) (choose *Individual* unless you publish as a company; the sign-up page shows the current fee). Verification can take a day or two.
2. **Reserve the name.** Partner Center → **Apps and games** → **New product** → **MSIX or PWA app** → enter *CPUx* (or an alternative from `store/listing.md` if it's taken).
3. **Copy your identity.** Open the new app → **Product management** → **Product identity**. Copy these values into `packaging/store/identity.json` (and set `DisplayName` to the exact name you reserved):
   - `Package/Identity/Name` → `IdentityName`
   - `Package/Identity/Publisher` → `Publisher` (starts with `CN=`)
   - `Package/Properties/PublisherDisplayName` → `PublisherDisplayName`
4. **Build the package:** `powershell -ExecutionPolicy Bypass -File packaging\build.ps1 -Only Msix`. The warning about placeholder values should be gone.
5. **Privacy policy.** It lives in `docs/privacy.md` and GitHub Pages publishes it at https://kuldipgami.github.io/CPUx/privacy/. Edit that file and push to update it.
6. **Start a submission** and fill in each section:
   - **Pricing and availability:** free or paid, and the markets.
   - **Properties:** category *Utilities & tools*, the privacy policy URL, your support contact, and the system requirements from `listing.md`.
   - **Age ratings:** answer the questionnaire (no violence, no user-to-user interaction, no purchases).
   - **Packages:** upload `artifacts/store/CPUx_<version>.0_x64.msix`.
   - **Store listings → English:** paste the description, "What's new", features and search terms from `store/listing.md`, then upload the screenshots in order.
   - **Submission options → Notes for certification:** paste the text below.
7. **Submit.** Certification usually takes 1–3 business days. Partner Center emails you the result. If something is rejected, the report says what to change.

### Restricted capabilities

By default the package declares one restricted capability, `runFullTrust`, which every Win32 desktop app in the Store needs. Partner Center asks you to justify it under **Submission options → Restricted capabilities**. Paste this:

> **runFullTrust:** CPUx is a Win32 desktop (WPF) application. It reads hardware information through Windows desktop APIs (performance counters, WMI, DXGI/Direct3D, device I/O controls) and controls fans through the laptop vendor's firmware interface, all of which require a full-trust desktop process.

`allowElevation`, which "Restart as administrator" needs inside a Store package, is **left out by default**. Microsoft approves it only under strict criteria and asks developers to email reportapp@microsoft.com with a justification before submitting. Without it, the Store version runs as a standard user: the "Restart as administrator" buttons are hidden, and CPU voltages, motherboard sensors and SMART data are unavailable there. Fan control on ASUS laptops and almost all other readings still work. If Microsoft agrees to approve it, build with `build.ps1 -Only Msix -AllowElevation` and add a second justification: *"allowElevation: the optional Restart as administrator button relaunches CPUx elevated, because Windows only exposes CPU voltage, motherboard sensor and drive SMART readings to administrators. CPUx never elevates unless the user clicks that button."*

### Notes for certification

> CPUx needs no account or sign-in. All features are available right after launch from the sidebar.
> • Fan control works only on supported hardware (ASUS laptops and supported desktop graphics cards). On other PCs the page explains that fans can't be controlled, which is expected.
> • The Stress test page intentionally puts the CPU/GPU/memory/disk under full load until the chosen duration ends or the user presses Stop.
> • Some sensors (CPU voltages, motherboard, drive health) show as unavailable without administrator rights, which is expected in the Store version.
> • The app makes no network connections.

### Testing the MSIX before uploading (optional)

`build.ps1 -Only Msix -TestSign` signs the package with a self-signed certificate that matches your Publisher and saves it as `artifacts/store/CPUx-test-signing.cer`. To install the package on your PC, import that `.cer` into **Local Machine → Trusted People** (double-click it → Install Certificate). This step needs administrator rights. Then double-click the `.msix`. Uninstall the test package from Settings › Apps before installing the Store version.

### Releasing an update

Raise `<Version>` in `src/CPUx/CPUx.csproj` (for example 1.1.0 → 1.2.0), run `build.ps1`, then create a new submission in Partner Center with the new `.msix` and updated "What's new" text.
