# CPUx

A modern hardware information and live system monitor for Windows 10/11, built with WPF on .NET 8.

![CPUx dashboard](docs/dashboard.png)

## Features

| Page | What you get |
| --- | --- |
| **Dashboard** | A live tile with a history graph for the processor, **every GPU** (integrated and discrete), memory, **every drive** — including USB flash drives and SD cards, which appear and disappear as you plug them in — network and battery. Plus a system summary, vitals, top processes and volume usage. Click any tile to drill in. |
| **Processor** | Model, codename, vendor, cores/threads (P-core / E-core split on hybrid CPUs), caches per level, CPUID family/model/stepping, microcode, instruction-set support (SSE…AVX-512, AES, SHA, VT-x…), per-logical-processor usage and clock tiles, utilization graph, processes/threads/handles, context switches, interrupts. |
| **Graphics** | Every adapter (discrete + integrated): per-engine utilization (3D, Copy, Video Encode/Decode, Compute…), dedicated/shared memory, temperature, hot spot, memory junction, core/memory clocks, power vs. limit, fan, P-state, PCIe link, encoder/decoder load, throttle reason, driver details, and NVIDIA specifics via NVML (architecture, CUDA cores, compute capability, VBIOS, power limits…). |
| **Memory** | Usage graph, composition bar (in use / modified / standby / free), committed, cached, compressed, paged/non-paged pools, hardware reserved, page faults, every module (vendor, part number, speed, voltage, rank, slot), page files. |
| **Storage** | Each drive with type (NVMe/SATA/SSD/HDD), health, active-time and read/write graphs, response time, temperature, life remaining, SMART reliability counters (admin), partitions and volume usage. |
| **Network** | Total and per-adapter throughput graphs, IPv4/IPv6, gateway, DNS, DHCP, MAC, MTU, link speed, Wi-Fi details. Virtual adapters can be shown or hidden. |
| **Motherboard** | System, board and BIOS/UEFI details, firmware type, Secure Boot, plus Super I/O / EC sensors when available. |
| **Battery** | Charge ring, charge/discharge power, voltage, remaining / full / design capacity, wear level, health, cycle count, chemistry, manufacturer. |
| **Displays** | Arrangement preview and, per monitor: EDID name, manufacturer, size, PPI, resolution, refresh rate, scaling, HDR support, bit depth, connection type. |
| **Sensors** | Every LibreHardwareMonitor sensor (temperatures, clocks, loads, voltages, power, fans…) with min/max, filtering and reset, plus ACPI thermal zones. |
| **Processes** | Task-manager style list with icons, CPU, memory, commit, disk I/O, GPU, threads, handles; live sorting, search, end task, open file location. |
| **Devices** | All Plug and Play devices grouped by class, with search and problem-device highlighting. |
| **Operating System** | Edition, version/build, install date, uptime, locale, power plan, Secure Boot, VBS/HVCI, TPM, Defender status, runtime info. |
| **Fan control** | Every fan with live RPM and a graph. Switch any fan to Manual and set its speed with a slider, or leave it on Automatic. ASUS laptops also get Silent / Performance / Turbo mode. Includes a safety fallback and restores automatic control when CPUx closes. |
| **Stress test** | Full-load stability testing for CPU, GPU, memory and storage — individually or all at once — with live temperature, utilization, clock and power graphs, automatic safety stops, error detection, an event log and a results summary (scores, peak temperatures/power, throttling). |

### Stress test details

| Component | Workload | How errors are detected |
| --- | --- | --- |
| **CPU** | FP64 FMA on AVX-512 / AVX2 / SIMD, integer hashing in L2 cache, or both; all threads, physical cores or a single thread | Every calculation is compared bit-for-bit with a reference result |
| **GPU** | Direct3D 11 compute shader running dense FP32 multiply-add chains, plus optional large video-memory copies; pick any adapter | Results are read back every 2 s and compared with the first run; driver resets (TDR) count as failures |
| **Memory** | Fills 50–85% of free RAM and cycles solid, alternating, address and pseudo-random patterns | Every word is verified after it is written |
| **Storage** | Temporary 1–16 GB file: sequential write, sequential read, then 20 s of random 4K I/O, bypassing the OS cache | Every block read back is checked against its expected pattern; the file is deleted when the test stops |

### Fan control details

| Hardware | How | Needs |
| --- | --- | --- |
| **ASUS laptops** (ROG, TUF, Zephyrus, Vivobook…) | Fan curves and performance mode through the ASUS firmware interface — the same one Armoury Crate uses | The "ASUS System Control Interface" driver (installed with Windows Update / MyASUS); no admin |
| **Desktop motherboards** | Super I/O fan headers via LibreHardwareMonitor | Run as administrator + PawnIO driver |
| **Desktop graphics cards** | GPU cooler control via LibreHardwareMonitor (NVIDIA / AMD) | Supported card |

Other laptops keep fan control inside their firmware, so their fans can be monitored but not set. A manual speed is applied as a flat fan curve; **Automatic** gives the fan back to the firmware (on ASUS by re-applying the current performance mode). With the safety option on, a fan set slower than the firmware would run returns to automatic when the CPU reaches 95 °C or the GPU 85 °C. ASUS laptops also report the CPU temperature through the same interface, so CPU temperature (and the stress test's CPU safety stop) works there without admin rights.

Tests stop automatically when the duration is reached, when the CPU or GPU hits the temperature limit you chose, or (optionally) at the first error. The CPU temperature limit needs administrator rights and the PawnIO driver, because that's what Windows requires to read the CPU's temperature sensor.

Also included:

- **Dark, light or system theme**, six accent colors, °C/°F.
- **Mini monitor**: a compact always-on-top overlay (Ctrl+M).
- **Tray icon** with live tooltip, optional minimize-to-tray, start with Windows, and **high-temperature notifications**.
- **Export** a full report as HTML, JSON or plain text (Ctrl+E).
- Pause/resume (Ctrl+P), always-on-top, adjustable update interval and graph history.

## Install

| File | Use |
| --- | --- |
| `artifacts\installer\CPUx-Setup-1.1.0-x64.msi` | **Installer** — double-click to install on any 64-bit Windows 10/11 PC (.NET included). Adds Start menu and desktop shortcuts; uninstall from Settings › Apps. |
| `artifacts\store\CPUx_1.1.0.0_x64.msix` | **Microsoft Store package** — upload it in Partner Center. See [packaging/README.md](packaging/README.md) for the step-by-step guide. |
| `artifacts\portable\CPUx.exe` | **Portable** single file, no install; needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0). |

### Getting every sensor

Most data works as a normal user. CPU temperatures, voltages, per-core clocks, motherboard fans and drive SMART data need two extra things:

1. **Run as administrator.** Use the sidebar link "Restart as administrator".
2. **Install the [PawnIO](https://pawnio.eu) driver.** LibreHardwareMonitor uses it for low-level hardware access.

### Command-line options

| Option | Effect |
| --- | --- |
| `--page "<name>"` | Open a specific page, e.g. `--page Graphics` |
| `--mini` | Also open the mini monitor |
| `--minimized` | Start minimized (used by "Start with Windows") |
| `--export <file>` | Write a report without showing the UI and exit. Format follows the extension: `.html`, `.json` or `.txt` |

## Build

Requires the .NET 8 SDK.

```
dotnet build src/CPUx/CPUx.csproj
powershell -ExecutionPolicy Bypass -File packaging\build.ps1
```

The second command builds the installer, the Store package and the portable exe into `artifacts\` (see [packaging/README.md](packaging/README.md)).

## Project layout

```
src/CPUx/
  Controls/     LiveChart, RingGauge, StackedBar, MasonryPanel, Icon (custom-drawn, no chart library)
  Core/         settings, theming, formatting, WMI helper, native interop
  Services/     samplers (perf counters, NVML, LibreHardwareMonitor, process list),
                hardware readers (CPUID, DXGI, EDID, battery IOCTL, storage), report exporter, tray
  ViewModels/   one view model per page (CommunityToolkit.Mvvm)
  Views/        XAML pages
  Themes/       Dark/Light palettes, styles, icons, shared templates
packaging/      installer (WiX), MSIX manifest and assets, Store listing, build script
```

Settings and logs are stored in `%AppData%\CPUx\settings.json` and `%LocalAppData%\CPUx\cpux.log`.

## Credits

Sensor data comes from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0). NVIDIA data comes from NVML, which ships with the NVIDIA driver.

## License

[MIT](LICENSE) © 2026 Kuldip Gami. Privacy policy: [kuldipgami.github.io/CPUx/privacy](https://kuldipgami.github.io/CPUx/privacy/).
