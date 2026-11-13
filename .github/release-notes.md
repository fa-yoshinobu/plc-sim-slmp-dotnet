## Download
- [Windows x64 ZIP]({{DOWNLOAD_URL}})
- [SHA-256 checksum]({{CHECKSUM_URL}})

## Getting started
Extract the ZIP file and run `PlcSim.Slmp/PlcSim.Slmp.Gui.exe`. Keep the companion folders together. The console executable is `PlcSim.Slmp/PlcSim.Slmp.Host.exe`.

Requirements: Windows 10 or later (x64). The .NET runtime is included.

## Changes
### Added
- Add bounded diagnostic logs for startup failures, server errors and script failures, with a GUI button to open the log folder.
- Add CI for pushes and pull requests, release content checks and a scheduled check for bundled runtime updates.

### Changed
- Bundle .NET 10.0.14, including the Windows Desktop and ASP.NET Core runtimes.
- Validate HTTP API Host and Origin headers. Non-loopback API listeners require an `api.token`; clients must send `Authorization: Bearer <token>`. Locally launched Python scripts receive the token automatically.
- Limit UDP sessions to 1,024 entries and expire sessions after 10 minutes without requests. Expired or evicted sessions start with fresh authentication and monitor state.
- Separate memory viewer formatting, editing, filtering and CSV output into independently tested components.
- Publish the executable, fixed-name distribution ZIP and SHA-256 checksum under `artifacts/release/` in local builds and CI.

### Fixed
- Report invalid configuration fields, including null label definitions. The GUI falls back to defaults; the console prints the reason and exits with code 1.
- Save configuration through a temporary file and include the configuration reference in the distribution ZIP.
- Preserve CSV values containing commas, quotes and line breaks.
- Diagnose malformed TCP requests and keep the listener available for subsequent connections.
- Reject invalid HTTP write bodies with status 400 and fix Python script state and immediate-exit races.
- Reject incomplete VirusTotal responses without a matching API-provided executable SHA-256.
- Stop failed builds immediately and exclude stale content, release metadata and Python caches from the distribution ZIP.

<!-- virustotal:start -->
{{VIRUSTOTAL_CONTENT}}
<!-- virustotal:end -->
