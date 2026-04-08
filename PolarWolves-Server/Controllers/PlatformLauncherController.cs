using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using PolarWolves_Globals;

namespace PolarWolves_Server.Controllers;

[ApiController]
[SupportedOSPlatform("windows")]
[Route("api/platform-launchers")]
public class PlatformLauncherController : ControllerBase
{
    private static readonly PlatformLauncherDefinition[] Launchers =
    [
        new("ubisoft", "Ubisoft", new[] { "uplay://" }, new[] { "Ubisoft Connect", "Uplay" }, new[] { "UbisoftConnect.exe", "upc.exe" }, new[] { @"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe", @"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\upc.exe" }, "https://www.ubisoft.com/en-gb/ubisoft-connect/download"),
        new("epic", "Epic Games", new[] { "com.epicgames.launcher://" }, new[] { "Epic Games Launcher", "Epic Games" }, new[] { "EpicGamesLauncher.exe" }, new[] { @"%ProgramFiles%\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe" }, "https://store.epicgames.com/download"),
        new("rockstar", "Rockstar", new[] { "rockstar://" }, new[] { "Rockstar Games Launcher" }, new[] { "Launcher.exe", "LauncherPatcher.exe" }, new[] { @"%ProgramFiles%\Rockstar Games\Launcher\Launcher.exe", @"%ProgramFiles%\Rockstar Games\Launcher\LauncherPatcher.exe" }, "https://socialclub.rockstargames.com/rockstar-games-launcher"),
        new("battlenet", "BattleNet", new[] { "battlenet://" }, new[] { "Battle.net" }, new[] { "Battle.net.exe", "Battle.net Launcher.exe" }, new[] { @"%ProgramFiles(x86)%\Battle.net\Battle.net Launcher.exe", @"%ProgramFiles(x86)%\Battle.net\Battle.net.exe", @"%ProgramFiles%\Battle.net\Battle.net Launcher.exe", @"%ProgramFiles%\Battle.net\Battle.net.exe" }, "https://download.battle.net/en-us/?platform=windows"),
        new("gog", "GOG Galaxy", new[] { "goggalaxy://" }, new[] { "GOG GALAXY", "GOG Galaxy" }, new[] { "GalaxyClient.exe" }, new[] { @"%ProgramFiles(x86)%\GOG Galaxy\GalaxyClient.exe", @"%ProgramFiles%\GOG Galaxy\GalaxyClient.exe" }, "https://www.gog.com/galaxy"),
        new("riot", "Riot Games", new[] { "riotclient://" }, new[] { "Riot Client", "Riot Games" }, new[] { "RiotClientServices.exe" }, new[] { @"%LocalAppData%\Riot Games\Riot Client\RiotClientServices.exe", @"%ProgramFiles%\Riot Games\Riot Client\RiotClientServices.exe" }, "https://support-leagueoflegends.riotgames.com/hc/en-us/articles/4406274299411-Riot-Client-FAQ"),
        new("ea", "EA Desktop", new[] { "eaapp://", "origin2://", "link2ea://" }, new[] { "EA app", "EA Desktop" }, new[] { "EADesktop.exe" }, new[] { @"%ProgramFiles%\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe", @"%ProgramFiles%\EA Games\EA Desktop\EADesktop.exe", @"%LocalAppData%\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe" }, "https://www.ea.com/ea-app")
    ];

    private static readonly string[] UninstallRegistryRoots =
    [
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    private static readonly string[] AppPathRegistryRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"
    ];

    private static readonly object PlatformsJsonLock = new();
    private static JObject? _platformsJson;

    [HttpPost("{platformId}")]
    public IActionResult Launch(string platformId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Ok(new PlatformLaunchResponse { Success = false, Message = "Launcher opening is only supported on Windows." });
        }

        var launcher = Launchers.FirstOrDefault(x => x.Id.Equals(platformId ?? "", StringComparison.OrdinalIgnoreCase));
        if (launcher is null)
        {
            return NotFound(new PlatformLaunchResponse { Success = false, Message = "Unsupported platform." });
        }

        try
        {
            if (TryLaunchLauncher(launcher, out var launchedBy))
            {
                return Ok(new PlatformLaunchResponse { Success = true, Message = launchedBy });
            }

            if (TryLaunchDownloadPage(launcher, out var openedBy))
            {
                return Ok(new PlatformLaunchResponse
                {
                    Success = true,
                    Message = openedBy
                });
            }

            return Ok(new PlatformLaunchResponse
            {
                Success = false,
                Message = $"{launcher.PlatformName} launcher was not found."
            });
        }
        catch (Exception ex)
        {
            Globals.WriteToLog($"Failed to open launcher for {launcher.PlatformName}.", ex);
            return Ok(new PlatformLaunchResponse { Success = false, Message = ex.Message });
        }
    }

    private static bool TryLaunchLauncher(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        if (TryLaunchRegisteredProtocol(launcher, out launchedBy)) return true;
        if (TryLaunchFromProtocolCommand(launcher, out launchedBy)) return true;
        if (TryLaunchFromAppPaths(launcher, out launchedBy)) return true;
        if (TryLaunchFromUninstallRegistry(launcher, out launchedBy)) return true;
        if (TryLaunchFromKnownPaths(launcher, out launchedBy)) return true;
        if (TryLaunchFromPlatformsJson(launcher, out launchedBy)) return true;
        if (TryLaunchFromStartMenuShortcut(launcher, out launchedBy)) return true;

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchDownloadPage(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        if (TryLaunchShellTarget(launcher.DownloadUrl))
        {
            launchedBy = "download-page";
            return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchRegisteredProtocol(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        foreach (var protocolUri in launcher.ProtocolUris)
        {
            var scheme = GetProtocolScheme(protocolUri);
            if (string.IsNullOrWhiteSpace(scheme) || !IsProtocolRegistered(scheme)) continue;
            var command = GetProtocolCommand(scheme);
            var executable = NormalizeExecutablePath(command);
            if (!IsExpectedLauncherExecutable(launcher, executable)) continue;
            if (!TryLaunchShellTarget(protocolUri)) continue;

            launchedBy = $"protocol:{scheme}";
            return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromProtocolCommand(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        foreach (var protocolUri in launcher.ProtocolUris)
        {
            var scheme = GetProtocolScheme(protocolUri);
            if (string.IsNullOrWhiteSpace(scheme)) continue;

            var command = GetProtocolCommand(scheme);
            var executable = ExtractExecutablePath(command);
            if (!TryLaunchExpectedExecutable(launcher, executable)) continue;

            launchedBy = $"protocol-command:{scheme}";
            return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromAppPaths(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        foreach (var appPathRoot in AppPathRegistryRoots)
        {
            if (TryLaunchFromAppPathRegistryRoot(Registry.LocalMachine, appPathRoot, launcher, out launchedBy)) return true;
            if (TryLaunchFromAppPathRegistryRoot(Registry.CurrentUser, appPathRoot, launcher, out launchedBy)) return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromAppPathRegistryRoot(RegistryKey hive, string rootPath, PlatformLauncherDefinition launcher, out string launchedBy)
    {
        foreach (var exeName in launcher.ExecutableNames)
        {
            using var appKey = hive.OpenSubKey(Path.Join(rootPath, exeName));
            var executable = appKey?.GetValue("")?.ToString();
            if (!TryLaunchExpectedExecutable(launcher, executable)) continue;

            launchedBy = "app-paths";
            return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromUninstallRegistry(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        foreach (var root in UninstallRegistryRoots)
        {
            if (TryLaunchFromUninstallRegistryRoot(Registry.LocalMachine, root, launcher, out launchedBy)) return true;
            if (TryLaunchFromUninstallRegistryRoot(Registry.CurrentUser, root, launcher, out launchedBy)) return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromUninstallRegistryRoot(RegistryKey hive, string rootPath, PlatformLauncherDefinition launcher, out string launchedBy)
    {
        using var rootKey = hive.OpenSubKey(rootPath);
        if (rootKey is null)
        {
            launchedBy = "";
            return false;
        }

        foreach (var subKeyName in rootKey.GetSubKeyNames())
        {
            using var subKey = rootKey.OpenSubKey(subKeyName);
            if (subKey is null) continue;

            var displayName = subKey.GetValue("DisplayName")?.ToString() ?? "";
            if (!DisplayNameMatches(launcher, displayName))
                continue;

            var displayIcon = subKey.GetValue("DisplayIcon")?.ToString();
            if (TryLaunchExpectedExecutable(launcher, ExtractExecutablePath(displayIcon)))
            {
                launchedBy = "registry:DisplayIcon";
                return true;
            }

            var installLocation = Globals.ExpandEnvironmentVariables(subKey.GetValue("InstallLocation")?.ToString() ?? "");
            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                foreach (var exeName in launcher.ExecutableNames)
                {
                    if (!TryLaunchExpectedExecutable(launcher, Path.Join(installLocation, exeName))) continue;
                    launchedBy = "registry:InstallLocation";
                    return true;
                }
            }
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromKnownPaths(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        foreach (var candidate in launcher.KnownExecutablePaths)
        {
            if (!TryLaunchExpectedExecutable(launcher, candidate)) continue;
            launchedBy = "known-path";
            return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromPlatformsJson(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        var platformJson = GetPlatformJson(launcher.PlatformName);
        var defaultExe = platformJson?["ExeLocationDefault"]?.ToString();
        if (TryLaunchExecutable(Globals.ExpandEnvironmentVariables(defaultExe ?? "")))
        {
            launchedBy = "platforms.json:ExeLocationDefault";
            return true;
        }

        launchedBy = "";
        return false;
    }

    private static bool TryLaunchFromStartMenuShortcut(PlatformLauncherDefinition launcher, out string launchedBy)
    {
        var platformJson = GetPlatformJson(launcher.PlatformName);
        var shortcutNames = new HashSet<string>(launcher.DisplayNameHints, StringComparer.OrdinalIgnoreCase);
        var shortcutConfig = platformJson?["GetPathFromShortcutNamed"]?.ToString() ?? "";

        foreach (var configuredName in shortcutConfig.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            shortcutNames.Add(configuredName);

        var startMenuRoots = new[]
        {
            Globals.ExpandEnvironmentVariables("%StartMenuProgramData%"),
            Globals.ExpandEnvironmentVariables("%StartMenuAppData%")
        };

        foreach (var startMenuRoot in startMenuRoots)
        {
            if (!Directory.Exists(startMenuRoot)) continue;

            foreach (var shortcutName in shortcutNames)
            {
                foreach (var shortcutPath in FindShortcutCandidates(startMenuRoot, shortcutName))
                {
                    var target = Globals.GetShortcutTarget(shortcutPath);
                    if (!TryLaunchExpectedExecutable(launcher, target)) continue;

                    launchedBy = "shortcut";
                    return true;
                }
            }
        }

        launchedBy = "";
        return false;
    }

    private static IEnumerable<string> FindShortcutCandidates(string startMenuRoot, string shortcutName)
    {
        IEnumerable<string> exactMatches;
        try
        {
            exactMatches = Directory.EnumerateFiles(startMenuRoot, shortcutName + ".lnk", SearchOption.AllDirectories);
        }
        catch
        {
            exactMatches = Array.Empty<string>();
        }

        foreach (var exactMatch in exactMatches)
            yield return exactMatch;

        IEnumerable<string> allShortcuts;
        try
        {
            allShortcuts = Directory.EnumerateFiles(startMenuRoot, "*.lnk", SearchOption.AllDirectories);
        }
        catch
        {
            allShortcuts = Array.Empty<string>();
        }

        foreach (var shortcutPath in allShortcuts)
        {
            var fileName = Path.GetFileNameWithoutExtension(shortcutPath);
            if (!fileName.Contains(shortcutName, StringComparison.OrdinalIgnoreCase)) continue;
            yield return shortcutPath;
        }
    }

    private static bool TryLaunchShellTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Globals.WriteToLog($"Failed to open shell target '{target}'.", ex);
            return false;
        }
    }

    private static bool TryLaunchExecutable(string executablePath)
    {
        var normalized = NormalizeExecutablePath(executablePath);
        if (string.IsNullOrWhiteSpace(normalized) || !System.IO.File.Exists(normalized)) return false;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = normalized,
                WorkingDirectory = Path.GetDirectoryName(normalized) ?? "",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            Globals.WriteToLog($"Failed to open executable '{normalized}'.", ex);
            return false;
        }
    }

    private static bool TryLaunchExpectedExecutable(PlatformLauncherDefinition launcher, string? executablePath)
    {
        var normalized = NormalizeExecutablePath(executablePath);
        if (!IsExpectedLauncherExecutable(launcher, normalized)) return false;
        return TryLaunchExecutable(normalized);
    }

    private static bool IsProtocolRegistered(string scheme)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(scheme);
        return key is not null;
    }

    private static string GetProtocolCommand(string scheme)
    {
        return Registry.GetValue($@"HKEY_CLASSES_ROOT\{scheme}\shell\open\command", "", null)?.ToString() ?? "";
    }

    private static string GetProtocolScheme(string protocolUri)
    {
        if (string.IsNullOrWhiteSpace(protocolUri)) return "";
        var separatorIndex = protocolUri.IndexOf(':');
        return separatorIndex > 0 ? protocolUri[..separatorIndex] : protocolUri.Trim();
    }

    private static string NormalizeExecutablePath(string rawPath)
    {
        var extracted = ExtractExecutablePath(rawPath);
        return string.IsNullOrWhiteSpace(extracted)
            ? ""
            : Globals.ExpandEnvironmentVariables(extracted.Trim().Trim('"'));
    }

    private static string ExtractExecutablePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return "";

        var value = rawPath.Trim();
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote > 1)
                return value.Substring(1, closingQuote - 1);
        }

        var exeIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
            return value[..(exeIndex + 4)];

        var commaIndex = value.IndexOf(',');
        return commaIndex > 0 ? value[..commaIndex] : value;
    }

    private static bool IsExpectedLauncherExecutable(PlatformLauncherDefinition launcher, string? executablePath)
    {
        var normalized = NormalizeExecutablePath(executablePath ?? "");
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        var fileName = Path.GetFileName(normalized);
        return launcher.ExecutableNames.Any(expected => fileName.Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DisplayNameMatches(PlatformLauncherDefinition launcher, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return false;

        return launcher.DisplayNameHints.Any(hint =>
            displayName.Equals(hint, StringComparison.OrdinalIgnoreCase) ||
            displayName.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static JObject? GetPlatformJson(string platformName)
    {
        var platforms = GetPlatformsJson();
        return platforms?["Platforms"]?[platformName] as JObject;
    }

    private static JObject? GetPlatformsJson()
    {
        if (_platformsJson is not null) return _platformsJson;

        lock (PlatformsJsonLock)
        {
            if (_platformsJson is not null) return _platformsJson;

            foreach (var candidate in GetPlatformsJsonCandidates())
            {
                if (!System.IO.File.Exists(candidate)) continue;
                _platformsJson = JObject.Parse(System.IO.File.ReadAllText(candidate));
                return _platformsJson;
            }

            return null;
        }
    }

    private static IEnumerable<string> GetPlatformsJsonCandidates()
    {
        yield return Path.Join(Globals.UserDataFolder, "Platforms.json");
        yield return Path.Join(Globals.AppDataFolder, "Platforms.json");
        yield return Path.Join(AppContext.BaseDirectory, "Platforms.json");
    }

    private sealed record PlatformLauncherDefinition(
        string Id,
        string PlatformName,
        IReadOnlyList<string> ProtocolUris,
        IReadOnlyList<string> DisplayNameHints,
        IReadOnlyList<string> ExecutableNames,
        IReadOnlyList<string> KnownExecutablePaths,
        string DownloadUrl
    );

    public sealed class PlatformLaunchResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }
}
