using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Win32;
using PolarWolves_Globals;
using PolarWolves_Server.Data;
using PolarWolves_Server.Pages.Steam;
using SteamSettings = PolarWolves_Server.Data.Settings.Steam;
using SteamIndex = PolarWolves_Server.Pages.Steam.Index;

namespace PolarWolves_Server.Controllers;

[ApiController]
[Route("api/steam")]
public class SteamBridgeController : ControllerBase
{
    private const ulong SteamId64Base = 76561197960265728;
    private static readonly HttpClient AvatarHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private static readonly Regex AvatarFullRegex = new(
        @"<avatarFull>\s*<!\[CDATA\[(.*?)\]\]>\s*</avatarFull>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts()
    {
        if (!EnsureSteamFolder(out var error, requireLoginUsers: false))
        {
            return Ok(new SteamAccountsResponse
            {
                Success = false,
                Message = error
            });
        }

        var loginUsersPath = GetSteamLoginUsersPath();
        if (!System.IO.File.Exists(loginUsersPath))
        {
            AppData.SteamUsers = new List<SteamIndex.Steamuser>();
            return Ok(new SteamAccountsResponse
            {
                Success = true,
                Message = "No Steam accounts were detected. Import loginusers.vdf from Settings, or open Steam once.",
                CurrentSteamId = "",
                Accounts = new List<SteamAccountDto>()
            });
        }

        await SteamSwitcherFuncs.LoadProfiles().ConfigureAwait(false);

        var users = AppData.SteamUsers;
        if (users == null) users = new List<SteamIndex.Steamuser>();
        var currentAccountId = SteamSwitcherFuncs.GetCurrentAccountId();

        var accountTasks = users.Select(async user =>
        {
            var steamId = user.SteamId ?? "";
            var avatar = await ResolveAvatarUrlAsync(user).ConfigureAwait(false);
            return new SteamAccountDto
            {
                SteamId = steamId,
                AccountName = user.AccName ?? "",
                PersonaName = string.IsNullOrWhiteSpace(user.Name) ? (user.AccName ?? "") : user.Name,
                AvatarUrl = avatar,
                LastLoginTimestamp = ParseUnixTimestamp(user.LastLogin),
                IsCurrent = !string.IsNullOrWhiteSpace(currentAccountId) &&
                            string.Equals(steamId, currentAccountId, StringComparison.Ordinal)
            };
        });
        var accounts = (await Task.WhenAll(accountTasks).ConfigureAwait(false)).ToList();

        return Ok(new SteamAccountsResponse
        {
            Success = true,
            Message = "ok",
            CurrentSteamId = currentAccountId ?? "",
            Accounts = accounts
        });
    }

    [HttpPost("switch")]
    public IActionResult SwitchAccount([FromBody] SteamSwitchRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SteamId))
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "steamId is required."
            });
        }

        if (!EnsureSteamFolder(out var error, requireLoginUsers: true))
        {
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = error
            });
        }

        if (!SteamSwitcherFuncs.VerifySteamId(request.SteamId))
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Invalid SteamId."
            });
        }

        SteamSwitcherFuncs.SwapSteamAccounts(
            request.SteamId,
            request.PersonaState,
            accountOffline: request.AccountOffline
        );

        return Ok(new SteamActionResponse
        {
            Success = true,
            Message = "Steam switch requested."
        });
    }

    [HttpPost("new-login")]
    public IActionResult NewLogin()
    {
        if (!EnsureSteamFolder(out var error, requireLoginUsers: false))
        {
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = error
            });
        }

        SteamSwitcherFuncs.SwapSteamAccounts();
        return Ok(new SteamActionResponse
        {
            Success = true,
            Message = "Steam new login requested."
        });
    }

    [HttpPost("set-status")]
    public IActionResult SetStatus([FromBody] SteamSetStatusRequest request)
    {
        if (request == null)
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Request is required."
            });
        }

        var statusUri = PersonaStateToProtocolUri(request.PersonaState);
        if (string.IsNullOrWhiteSpace(statusUri))
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Unsupported Steam status."
            });
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = statusUri,
                UseShellExecute = true
            });

            return Ok(new SteamActionResponse
            {
                Success = true,
                Message = "Steam status requested."
            });
        }
        catch (Exception ex)
        {
            Globals.WriteToLog("Failed to set Steam status via protocol.", ex);
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = "Failed to set Steam status."
            });
        }
    }

    [HttpGet("owned-games/{steamId}")]
    public async Task<IActionResult> OwnedGames(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId) || !SteamSwitcherFuncs.VerifySteamId(steamId))
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Invalid SteamId."
            });
        }

        var payload = await SteamSwitcherFuncs.GetSteamOwnedGames(steamId).ConfigureAwait(false);
        return Content(payload, "application/json");
    }

    [HttpPost("backup/create")]
    public IActionResult CreateSteamBackup()
    {
        if (!EnsureSteamFolder(out var error, requireLoginUsers: true))
        {
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = error
            });
        }

        try
        {
            var result = WriteSteamBackupSnapshot("steam_backup");
            return Ok(new SteamBackupActionResponse
            {
                Success = true,
                Message = "Steam backup created.",
                FileName = result.FileName,
                BackupCount = result.BackupCount,
                BackupPath = result.BackupPath
            });
        }
        catch (Exception ex)
        {
            Globals.WriteToLog("Failed to create Steam backup.", ex);
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = "Failed to create Steam backup."
            });
        }
    }

    [HttpGet("backup/list")]
    public IActionResult ListSteamBackups()
    {
        try
        {
            var backupRoot = GetSteamBackupRoot();
            Directory.CreateDirectory(backupRoot);

            var folderBackups = Directory
                .EnumerateDirectories(backupRoot, "steam_backup*", SearchOption.TopDirectoryOnly)
                .Select(path => new DirectoryInfo(path))
                .Select(dir => new SteamBackupListItem
                {
                    FileName = dir.Name,
                    LastWriteUtc = dir.LastWriteTimeUtc,
                    SizeBytes = GetDirectorySize(dir.FullName),
                    BackupPath = dir.FullName,
                    BackupType = "folder"
                });

            var legacyJsonBackups = Directory
                .EnumerateFiles(backupRoot, "steam_backup*.json", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Select(file => new SteamBackupListItem
                {
                    FileName = file.Name,
                    LastWriteUtc = file.LastWriteTimeUtc,
                    SizeBytes = file.Length,
                    BackupPath = file.FullName,
                    BackupType = "legacy-json"
                });

            var files = folderBackups
                .Concat(legacyJsonBackups)
                .OrderByDescending(item => item.LastWriteUtc)
                .ToList();

            return Ok(new SteamBackupListResponse
            {
                Success = true,
                Message = "ok",
                BackupRoot = backupRoot,
                Files = files
            });
        }
        catch (Exception ex)
        {
            Globals.WriteToLog("Failed to list Steam backups.", ex);
            return Ok(new SteamBackupListResponse
            {
                Success = false,
                Message = "Failed to list Steam backups."
            });
        }
    }

    [HttpPost("backup/restore")]
    public IActionResult RestoreSteamBackup([FromBody] SteamRestoreRequest request)
    {
        if (!EnsureSteamFolder(out var error, requireLoginUsers: false))
        {
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = error
            });
        }

        var fileName = request?.FileName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Backup file name is required."
            });
        }

        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Invalid backup file name."
            });
        }

        try
        {
            var backupRoot = GetSteamBackupRoot();
            var backupPath = Path.Join(backupRoot, fileName);
            if (!Directory.Exists(backupPath) && !System.IO.File.Exists(backupPath))
            {
                return Ok(new SteamActionResponse
                {
                    Success = false,
                    Message = "Backup was not found."
                });
            }

            _ = TryWriteSteamBackupSnapshot("steam_backup_before_restore");

            if (Directory.Exists(backupPath))
            {
                RestoreSteamFolderBackup(backupPath);
            }
            else
            {
                var json = System.IO.File.ReadAllText(backupPath);
                var snapshot = JsonSerializer.Deserialize<SteamBackupSnapshot>(json);
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.LoginUsersVdf))
                {
                    return Ok(new SteamActionResponse
                    {
                        Success = false,
                        Message = "Backup file is invalid."
                    });
                }

                RestoreSteamSnapshot(snapshot);
            }

            _ = SteamSwitcherFuncs.LoadProfiles();

            return Ok(new SteamActionResponse
            {
                Success = true,
                Message = "Steam backup restored."
            });
        }
        catch (Exception ex)
        {
            Globals.WriteToLog("Failed to restore Steam backup.", ex);
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = "Failed to restore Steam backup."
            });
        }
    }

    [HttpPost("config/import")]
    public async Task<IActionResult> ImportSteamConfig([FromForm] IFormFile file)
    {
        if (!EnsureSteamFolder(out var error, requireLoginUsers: false))
        {
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = error
            });
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Choose loginusers.vdf or config.vdf first."
            });
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            return BadRequest(new SteamActionResponse
            {
                Success = false,
                Message = "Steam config file is too large."
            });
        }

        var safeName = Path.GetFileName(file.FileName ?? "").Trim();
        var targetName = string.Equals(safeName, "config.vdf", StringComparison.OrdinalIgnoreCase)
            ? "config.vdf"
            : "loginusers.vdf";

        try
        {
            await using var stream = file.OpenReadStream();
            _ = TryWriteSteamBackupSnapshot("steam_backup_before_import");

            var configDir = Path.Join(SteamSettings.FolderPath, "config");
            Directory.CreateDirectory(configDir);
            await using (var output = System.IO.File.Create(Path.Join(configDir, targetName)))
            {
                await stream.CopyToAsync(output).ConfigureAwait(false);
            }

            if (System.IO.File.Exists(GetSteamLoginUsersPath()))
                _ = SteamSwitcherFuncs.LoadProfiles();
            return Ok(new SteamActionResponse
            {
                Success = true,
                Message = $"{targetName} imported into Steam."
            });
        }
        catch (Exception ex)
        {
            Globals.WriteToLog("Failed to import Steam config file.", ex);
            return Ok(new SteamActionResponse
            {
                Success = false,
                Message = "Failed to import Steam config file."
            });
        }
    }

    private static SteamBackupWriteResult WriteSteamBackupSnapshot(string prefix)
    {
        var loginUsersPath = GetSteamLoginUsersPath();
        if (!System.IO.File.Exists(loginUsersPath))
        {
            throw new FileNotFoundException("Steam loginusers.vdf was not found.", loginUsersPath);
        }

        var backupRoot = GetSteamBackupRoot();
        Directory.CreateDirectory(backupRoot);

        var backupName = GetUniqueBackupName(prefix);
        var backupPath = Path.Join(backupRoot, backupName);
        Directory.CreateDirectory(backupPath);

        var copiedFiles = new List<string>();
        var configDir = Path.Join(SteamSettings.FolderPath, "config");
        var backupConfigDir = Path.Join(backupPath, "config");
        if (Directory.Exists(configDir))
        {
            CopyDirectoryFiles(configDir, backupConfigDir, copiedFiles, "config");
        }

        var userdataPath = Path.Join(SteamSettings.FolderPath, "userdata");
        if (Directory.Exists(userdataPath))
        {
            foreach (var localConfigPath in Directory.EnumerateFiles(userdataPath, "localconfig.vdf", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(SteamSettings.FolderPath, localConfigPath);
                if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath)) continue;

                var destinationPath = Path.Join(backupPath, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? backupPath);
                System.IO.File.Copy(localConfigPath, destinationPath, overwrite: true);
                copiedFiles.Add(relativePath.Replace('\\', '/'));
            }
        }

        var manifest = new SteamBackupManifest
        {
            BackupFormat = "steam-file-copy-v1",
            CreatedAtUtc = DateTime.UtcNow,
            SteamFolderPath = SteamSettings.FolderPath,
            BackupName = backupName,
            Files = copiedFiles.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList()
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        System.IO.File.WriteAllText(Path.Join(backupPath, "manifest.json"), JsonSerializer.Serialize(manifest, options));

        return new SteamBackupWriteResult
        {
            FileName = backupName,
            BackupCount = copiedFiles.Count,
            BackupPath = backupPath
        };
    }

    private static SteamBackupWriteResult? TryWriteSteamBackupSnapshot(string prefix)
    {
        try
        {
            if (!Directory.Exists(SteamSettings.FolderPath)) return null;
            if (!System.IO.File.Exists(GetSteamLoginUsersPath())) return null;
            return WriteSteamBackupSnapshot(prefix);
        }
        catch (Exception ex)
        {
            Globals.WriteToLog("Skipped Steam safety backup.", ex);
            return null;
        }
    }

    private static string GetUniqueBackupName(string prefix)
    {
        var backupRoot = GetSteamBackupRoot();
        var baseName = $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}";
        var backupName = baseName;
        var suffix = 1;
        while (Directory.Exists(Path.Join(backupRoot, backupName)) || System.IO.File.Exists(Path.Join(backupRoot, backupName + ".json")))
        {
            backupName = $"{baseName}_{suffix++}";
        }

        return backupName;
    }

    private static void CopyDirectoryFiles(string sourceDir, string destinationDir, List<string> copiedFiles, string relativeRoot)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativeFromSource = Path.GetRelativePath(sourceDir, sourcePath);
            if (relativeFromSource.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeFromSource)) continue;

            var destinationPath = Path.Join(destinationDir, relativeFromSource);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDir);
            System.IO.File.Copy(sourcePath, destinationPath, overwrite: true);
            copiedFiles.Add(Path.Join(relativeRoot, relativeFromSource).Replace('\\', '/'));
        }
    }

    private static void RestoreSteamFolderBackup(string backupPath)
    {
        var configBackupDir = Path.Join(backupPath, "config");
        if (!Directory.Exists(configBackupDir) || !System.IO.File.Exists(Path.Join(configBackupDir, "loginusers.vdf")))
        {
            throw new FileNotFoundException("Backup is missing config\\loginusers.vdf.", Path.Join(configBackupDir, "loginusers.vdf"));
        }

        var configDir = Path.Join(SteamSettings.FolderPath, "config");
        Directory.CreateDirectory(configDir);
        CopyDirectoryFiles(configBackupDir, configDir, new List<string>(), "config");

        var userdataBackupDir = Path.Join(backupPath, "userdata");
        if (Directory.Exists(userdataBackupDir))
        {
            var userdataDir = Path.Join(SteamSettings.FolderPath, "userdata");
            Directory.CreateDirectory(userdataDir);
            CopyDirectoryFiles(userdataBackupDir, userdataDir, new List<string>(), "userdata");
        }
    }

    private static void RestoreSteamSnapshot(SteamBackupSnapshot snapshot)
    {
        var configDir = Path.Join(SteamSettings.FolderPath, "config");
        Directory.CreateDirectory(configDir);
        System.IO.File.WriteAllText(Path.Join(configDir, "loginusers.vdf"), snapshot.LoginUsersVdf);

        if (!string.IsNullOrWhiteSpace(snapshot.ConfigVdf))
        {
            System.IO.File.WriteAllText(Path.Join(configDir, "config.vdf"), snapshot.ConfigVdf);
        }

        if (snapshot.LocalConfigByUserId32 == null) return;

        foreach (var kv in snapshot.LocalConfigByUserId32)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || !kv.Key.All(char.IsDigit)) continue;
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;

            var localConfigPath = Path.Join(SteamSettings.FolderPath, "userdata", kv.Key, "config", "localconfig.vdf");
            Directory.CreateDirectory(Path.GetDirectoryName(localConfigPath) ?? "");
            System.IO.File.WriteAllText(localConfigPath, kv.Value);
        }
    }

    private static string ReadOptionalFile(string path) =>
        System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "";

    private static string GetSteamBackupRoot() =>
        Path.Join(Globals.UserDataFolder, "Backups", "Steam");

    private static string GetSteamLoginUsersPath() =>
        Path.Join(SteamSettings.FolderPath, "config", "loginusers.vdf");

    private static long GetDirectorySize(string directoryPath) =>
        Directory.Exists(directoryPath)
            ? Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;
    private static bool EnsureSteamFolder(out string error, bool requireLoginUsers)
    {
        error = "";
        var foundSteamFolder = false;

        foreach (var candidate in GetSteamPathCandidates())
        {
            if (!TryNormalizeSteamFolder(candidate, out var normalized)) continue;
            if (!IsSteamFolder(normalized)) continue;
            foundSteamFolder = true;

            SteamSettings.FolderPath = normalized;
            SteamSettings.SaveSettings();

            if (!requireLoginUsers || System.IO.File.Exists(GetSteamLoginUsersPath()))
                return true;
        }

        error = requireLoginUsers && foundSteamFolder
            ? "Steam was found, but config\\loginusers.vdf is missing. Import a Steam config backup from Settings, or open Steam once."
            : "Steam installation was not found. Open Steam once, then retry.";
        return false;
    }

    private static bool TryNormalizeSteamFolder(string? candidate, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        normalized = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = Path.GetDirectoryName(normalized) ?? "";

        normalized = normalized.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(normalized);
    }

    private static bool IsSteamFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;

        return System.IO.File.Exists(Path.Join(folderPath, "Steam.exe")) ||
               Directory.Exists(Path.Join(folderPath, "config")) ||
               Directory.Exists(Path.Join(folderPath, "steamapps"));
    }

    private static IEnumerable<string> GetSteamPathCandidates()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SteamSettings.FolderPath ?? "",
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"),
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Steam")
        };

        if (OperatingSystem.IsWindows())
        {
            AddRegistry(candidates, @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath");
            AddRegistry(candidates, @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe");
            AddRegistry(candidates, @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistry(candidates, @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath");
        }

        return candidates;
    }

    private static void AddRegistry(ISet<string> target, string key, string value)
    {
        var raw = Registry.GetValue(key, value, null)?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return;
        _ = target.Add(raw);
    }

    private static async Task<string> ResolveAvatarUrlAsync(SteamIndex.Steamuser user)
    {
        var normalized = NormalizeAvatarUrl(user.ImgUrl);
        if (!IsQuestionMarkAvatar(normalized)) return normalized;

        var steamId = user.SteamId ?? "";
        if (string.IsNullOrWhiteSpace(steamId)) return normalized;

        var localAvatar = GetLocalCachedAvatar(steamId);
        if (!string.IsNullOrWhiteSpace(localAvatar)) return localAvatar;

        var miniProfileAvatar = await FetchAvatarFromMiniProfileAsync(steamId).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(miniProfileAvatar)) return miniProfileAvatar;

        var remoteAvatar = await FetchAvatarFromSteamProfileXmlAsync(steamId).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(remoteAvatar)) return remoteAvatar;

        return normalized;
    }

    private static bool IsQuestionMarkAvatar(string avatarUrl) =>
        avatarUrl.Contains("QuestionMark", StringComparison.OrdinalIgnoreCase);

    private static string GetLocalCachedAvatar(string steamId)
    {
        foreach (var ext in new[] { ".jpg", ".png", ".jpeg" })
        {
            foreach (var root in GetAvatarRootCandidates())
            {
                var localPath = Path.Join(root, steamId + ext);
                if (!System.IO.File.Exists(localPath)) continue;
                return $"/img/profiles/steam/{steamId}{ext}";
            }
        }

        return "";
    }

    private static IEnumerable<string> GetAvatarRootCandidates()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Join("wwwroot", "img", "profiles", "steam"),
            Path.Join(Globals.UserDataFolder, "wwwroot", "img", "profiles", "steam"),
            Path.Join(AppContext.BaseDirectory, "wwwroot", "img", "profiles", "steam")
        };
        return roots;
    }

    private static string PersonaStateToProtocolUri(int personaState)
    {
        return personaState switch
        {
            0 => "steam://friends/status/offline",
            1 => "steam://friends/status/online",
            2 => "steam://friends/status/busy",
            3 => "steam://friends/status/away",
            4 => "steam://friends/status/snooze",
            5 => "steam://friends/status/lookingtotrade",
            6 => "steam://friends/status/lookingtoplay",
            7 => "steam://friends/status/invisible",
            _ => ""
        };
    }

    private static async Task<string> FetchAvatarFromSteamProfileXmlAsync(string steamId)
    {
        try
        {
            var profileXml = await AvatarHttpClient
                .GetStringAsync($"https://steamcommunity.com/profiles/{steamId}?xml=1")
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(profileXml)) return "";

            var match = AvatarFullRegex.Match(profileXml);
            if (!match.Success) return "";

            var avatarUrl = match.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(avatarUrl)) return "";
            return avatarUrl;
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string> FetchAvatarFromMiniProfileAsync(string steamId)
    {
        if (!ulong.TryParse(steamId, out var steamId64)) return "";
        if (steamId64 <= SteamId64Base) return "";
        var accountId = steamId64 - SteamId64Base;

        try
        {
            var json = await AvatarHttpClient
                .GetStringAsync($"https://steamcommunity.com/miniprofile/{accountId}/json")
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return "";

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("avatar_url", out var avatarElement)) return "";
            var avatarUrl = avatarElement.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(avatarUrl)) return "";
            return avatarUrl.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static long ParseUnixTimestamp(string timestampText)
    {
        if (long.TryParse(timestampText, out var unix)) return unix;
        return 0;
    }

    private static string NormalizeAvatarUrl(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "/img/QuestionMark.jpg";
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return raw;

        var path = raw.Replace("\\", "/");
        if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
        return path;
    }

    public class SteamAccountDto
    {
        public string SteamId { get; set; } = "";
        public string AccountName { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public long LastLoginTimestamp { get; set; }
        public bool IsCurrent { get; set; }
    }

    public class SteamAccountsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string CurrentSteamId { get; set; } = "";
        public List<SteamAccountDto> Accounts { get; set; } = new();
    }

    public class SteamSwitchRequest
    {
        public string SteamId { get; set; } = "";
        public int PersonaState { get; set; } = -1;
        public bool? AccountOffline { get; set; }
    }

    public class SteamSetStatusRequest
    {
        public int PersonaState { get; set; } = -1;
    }

    public class SteamRestoreRequest
    {
        public string FileName { get; set; } = "";
    }

    public class SteamBackupSnapshot
    {
        public DateTime CreatedAtUtc { get; set; }
        public string SteamFolderPath { get; set; } = "";
        public string LoginUsersVdf { get; set; } = "";
        public string ConfigVdf { get; set; } = "";
        public Dictionary<string, string> LocalConfigByUserId32 { get; set; } = new();
    }

    public class SteamBackupManifest
    {
        public string BackupFormat { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string SteamFolderPath { get; set; } = "";
        public string BackupName { get; set; } = "";
        public List<string> Files { get; set; } = new();
    }

    public class SteamBackupWriteResult
    {
        public string FileName { get; set; } = "";
        public int BackupCount { get; set; }
        public string BackupPath { get; set; } = "";
    }

    public class SteamBackupActionResponse : SteamActionResponse
    {
        public string FileName { get; set; } = "";
        public int BackupCount { get; set; }
        public string BackupPath { get; set; } = "";
    }

    public class SteamBackupListItem
    {
        public string FileName { get; set; } = "";
        public DateTime LastWriteUtc { get; set; }
        public long SizeBytes { get; set; }
        public string BackupPath { get; set; } = "";
        public string BackupType { get; set; } = "";
    }

    public class SteamBackupListResponse : SteamActionResponse
    {
        public string BackupRoot { get; set; } = "";
        public List<SteamBackupListItem> Files { get; set; } = new();
    }

    public class SteamActionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }
}
