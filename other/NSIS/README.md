# NSIS Installer Build Notes

This project builds a desktop installer (`Polar Account Switcher - Installer.exe`) from:

- `other/NSIS/nsis-build-x64.nsi`
- `PolarWolves-Client/bin/x64/Release/PolarWolves.7z`

## 1) Prepare installer artwork

Use one source image and generate both NSIS bitmaps:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ".\other\NSIS\img\Prepare-InstallerImages.ps1" -SourceImage "C:\path\to\your-image.png"
```

Generated files:

- `other/NSIS/img/HeaderImage.bmp` (150x57)
- `other/NSIS/img/SideBanner.bmp` (164x314)

Alternative helper:

```bat
other\NSIS\img\Convert.bat "C:\path\to\your-image.png"
```

## 2) Build installer

```powershell
"& \"${env:ProgramFiles(x86)}\NSIS\makensis.exe\" \".\other\NSIS\nsis-build-x64.nsi\""
```

The output is created as:

- `other/NSIS/Polar Account Switcher - Installer.exe`

## Optional: Override installer image/icon paths at compile time

The NSIS script supports these compile-time defines:

- `INSTALLER_ICON`
- `INSTALLER_HEADER_BMP`
- `INSTALLER_SIDEBANNER_BMP`

Example:

```powershell
"& \"${env:ProgramFiles(x86)}\NSIS\makensis.exe\" /DINSTALLER_ICON=\"img\icon.ico\" /DINSTALLER_HEADER_BMP=\"img\HeaderImage.bmp\" /DINSTALLER_SIDEBANNER_BMP=\"img\SideBanner.bmp\" \".\other\NSIS\nsis-build-x64.nsi\""
```
