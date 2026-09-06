# WinProvision Auto — System Card refinement

This revision keeps the working `/auto` pipeline and refines only the Auto UI/system information presentation.

## System information
- Windows edition/version is normalized using `CurrentBuildNumber` (Windows 11 builds >= 22000), avoiding the legacy `ProductName` value that can still say `Windows 10` on Windows 11.
- DisplayVersion/ReleaseId is shown when available.
- CPU name is read from `HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0\\ProcessorNameString`, with an environment fallback.
- Installed RAM uses `GetPhysicallyInstalledSystemMemory`.
- System disk uses the actual system directory root and `DriveInfo`.
- Architecture uses `RuntimeInformation.OSArchitecture`.

## UI
- Compact translucent card.
- WPF-UI `Desktop24` icon in the card header.
- Two-column aligned labels/values.
- Long CPU/disk values wrap without clipping.
- Mica backdrop retained.
