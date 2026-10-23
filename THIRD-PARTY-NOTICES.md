# Third-party notices

BetterVitals is MIT-licensed. It uses or ships the following third-party components under their own terms.

| Component | Use | License |
|---|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | Hardware sensor access (NuGet package) | MPL-2.0 |
| [DiskInfoToolkit](https://github.com/Blacktempel/DiskInfoToolkit) | Drive data for LibreHardwareMonitor | MPL-2.0 |
| [RAMSPDToolkit](https://github.com/Blacktempel/RAMSPDToolkit) | Memory module (SPD) data for LibreHardwareMonitor | MPL-2.0 |
| [HidSharp](https://software.seekye.com/hidsharp) | USB devices (coolers, fan hubs, power supplies) for LibreHardwareMonitor | Apache-2.0 |
| [Mono.Posix.NETStandard](https://github.com/mono/mono) | Dependency of HidSharp (not used on Windows) | MIT |
| System.Management, System.IO.Ports (.NET) | Dependencies of LibreHardwareMonitor | MIT |
| [PawnIO](https://pawnio.eu/) by namazso | Signed kernel driver used by LibreHardwareMonitor. BetterVitals bundles the official, unmodified `PawnIO_setup.exe` 2.2.0 and runs it only when you choose to install it. | Official build: proprietary freeware, redistributed unmodified with the author's permission. Source: GPL-2.0-or-later with an exception. |
| [PresentMon](https://github.com/GameTechDev/PresentMon) by Intel | Reference for the ETW present events used to measure FPS | MIT |
| Inter, Geist, Space Grotesk, JetBrains Mono, Rajdhani, Orbitron | Overlay fonts (subsets, embedded) | SIL Open Font License 1.1 |

The source code of the MPL-2.0 components is available at the links above.
