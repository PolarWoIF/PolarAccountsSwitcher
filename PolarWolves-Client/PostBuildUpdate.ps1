param (
    [string]$SolutionDir
)
Write-Output "Solution Directory: $SolutionDir"
Write-Host "Starting PostBuildUpdate.ps1 script"

# Define the URL for the GitHub API to get the latest release
$apiUrl = "https://api.github.com/repos/PolarWolves/PolarWolves/releases/latest"

# Define the local paths
$downloadPath = "LAST-PolarWolves_and_CEF.7z"
$extractPath = "OldVersion"

# Define 7z executable path (update this if 7z is installed in a different location)
$sevenZipPath = "C:\Program Files\7-Zip\7z.exe"

# Function to download file safely (no remote code execution)
function DownloadFile {
    param (
        [string]$Url,
        [string]$OutputPath
    )

    try {
        Invoke-WebRequest -Uri $Url -OutFile $OutputPath -Headers @{ "User-Agent" = "PowerShell" }
        Write-Output "- Downloaded file to $OutputPath"
    }
    catch {
        Write-Error "Failed to download $Url. $_"
        throw
    }
}


# Function to extract file
function ExtractFile {
    param (
        [string]$filePath,
        [string]$outputPath
    )
    try {
        & $sevenZipPath x "$filePath" -o"$outputPath" -y
        Write-Output "- Extracted file to $outputPath"
    } catch {
        Write-Error "Failed to extract file $filePath. $_"
    }
}

# -----------------------------------
Write-Host "Downloading and extract the last build (including CEF)..."
# -----------------------------------

# Fetch the latest release data
$response = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "PowerShell" }

# Find the asset with "and_CEF" in its name
$asset = $response.assets | Where-Object { $_.name -like "*and_CEF*" } | Select-Object -First 1

if ($null -ne $asset) {
    # Download the file
    Write-Host "- Downloading..."
    DownloadFile -Url $asset.browser_download_url -OutputPath $downloadPath

    # Create the extraction folder if it doesn't exist
    if (-not (Test-Path $extractPath)) {
        New-Item -ItemType Directory -Path $extractPath
    }

    # Extract the file
    Write-Host "- Extracting..."
    ExtractFile -filePath $downloadPath -outputPath $extractPath
} else {
    Write-Error "No asset with 'and_CEF' found in the latest release."
    throw
}

# -----------------------------------
Write-Host "Installing requirements for updater: ASP.NET Core, Desktop Runtime and WebView2 Runtime..."
# -----------------------------------
choco upgrade dotnet-9.0-aspnetruntime dotnet-9.0-desktopruntime webview2-runtime -y --no-progress

# -----------------------------------
Write-Host "Running the updater to create updates based on the differences..."
# -----------------------------------
$updaterPath = "updater\PolarWolves-Updater.exe"
$updaterArgs = "createupdate"

if (Test-Path $updaterPath) {
    Start-Process -FilePath $updaterPath -ArgumentList $updaterArgs -Wait
} else {
    Write-Error "Updater not found at $updaterPath"
    throw
}

Write-Host "- Using 7z to compress the contents of UpdateOutput"
$compressPath = "UpdateOutput"
$compressOutput = "UpdateDiff.7z"

if (Test-Path $compressPath) {
    & $sevenZipPath a -r $compressOutput "./$compressPath/*" -mx9
} else {
    Write-Error "UpdateOutput folder not found at $compressPath"
    throw
}

# -----------------------------------
Write-Host "Preparing .zip version of .7z release files..."
# -----------------------------------
Write-Host "- Extracting .7z"
ExtractFile -filePath "PolarWolves.7z" -outputPath "PolarWolvesUnzipped"

# Use 7z to compress the contents of PolarWolves
Write-Host "- Compressing as .zip"
Compress-Archive -Path "PolarWolvesUnzipped\*" -DestinationPath "PolarWolves.zip"


# -----------------------------------
Write-Host "Preparing for upload"
# -----------------------------------
New-Item -ItemType Directory -Path "upload" -Force

$filesToCopy = @(
    "Polar Account Switcher - Installer.exe",
    "PolarWolves.7z",
    "PolarWolves.zip",
    "PolarWolves_and_CEF.7z"
)

$tagName = $env:APPVEYOR_REPO_TAG_NAME

foreach ($file in $filesToCopy) {
    $extension = [System.IO.Path]::GetExtension($file)
    $filenameWithoutExtension = [System.IO.Path]::GetFileNameWithoutExtension($file)
    $newFilename = "${filenameWithoutExtension}_$tagName$extension"
    Copy-Item -Path $file -Destination "upload\$newFilename" -Force
}

Write-Output "Files copied and renamed successfully."

