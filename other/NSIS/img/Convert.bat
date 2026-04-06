@echo off
setlocal

set "SCRIPT_DIR=%~dp0"

if not "%~1"=="" goto USE_SINGLE_IMAGE

if exist "%SCRIPT_DIR%HeaderImage.png" if exist "%SCRIPT_DIR%SideBanner.png" (
  where magick >nul 2>nul
  if %ERRORLEVEL%==0 (
    magick "%SCRIPT_DIR%HeaderImage.png" BMP2:"%SCRIPT_DIR%HeaderImage.bmp"
    magick "%SCRIPT_DIR%SideBanner.png" BMP2:"%SCRIPT_DIR%SideBanner.bmp"
    echo Converted HeaderImage.png and SideBanner.png with ImageMagick.
    exit /b 0
  )
)

echo Usage: Convert.bat "path\to\your-image.png"
echo This will generate:
echo - HeaderImage.bmp (150x57)
echo - SideBanner.bmp (164x314)
echo.
echo Tip: You can still drop HeaderImage.png and SideBanner.png and run this file
echo if ImageMagick ^(magick^) is installed.
exit /b 1

:USE_SINGLE_IMAGE
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%Prepare-InstallerImages.ps1" ^
  -SourceImage "%~1" ^
  -HeaderOut "%SCRIPT_DIR%HeaderImage.bmp" ^
  -SideOut "%SCRIPT_DIR%SideBanner.bmp"
exit /b %ERRORLEVEL%
