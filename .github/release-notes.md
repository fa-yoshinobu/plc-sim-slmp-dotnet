## Download
- [Windows x64 ZIP]({{DOWNLOAD_URL}})
- [SHA-256 checksum]({{CHECKSUM_URL}})

## Getting started
Extract the ZIP file and run `PlcSim.Slmp/PlcSim.Slmp.Gui.exe`. Keep the companion folders together. The console executable is `PlcSim.Slmp/PlcSim.Slmp.Host.exe`.

Requirements: Windows 10 or later (x64). The .NET runtime is included.

## Changes
### Changed
- Publish the executable, fixed-name distribution ZIP and SHA-256 checksum under `artifacts/release/` in local builds and CI.

### Fixed
- Reject incomplete VirusTotal responses without a matching API-provided executable SHA-256.
- Stop failed builds immediately and exclude stale content, release metadata and Python caches from the distribution ZIP.

<!-- virustotal:start -->
{{VIRUSTOTAL_CONTENT}}
<!-- virustotal:end -->
