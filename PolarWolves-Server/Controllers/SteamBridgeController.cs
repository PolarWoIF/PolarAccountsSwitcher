using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        if (!EnsureSteamFolder(out var error))
        {
            return Ok(new SteamAccountsResponse
            {
                Success = false,
                Message = error
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

        if (!EnsureSteamFolder(out var error))
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

        SteamSwitcherFuncs.SwapSteamAccounts(request.SteamId, request.PersonaState);

        return Ok(new SteamActionResponse
        {
            Success = true,
            Message = "Steam switch requested."
        });
    }

    [HttpPost("new-login")]
    public IActionResult NewLogin()
    {
        if (!EnsureSteamFolder(out var error))
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

    private static bool EnsureSteamFolder(out string error)
    {
        error = "";
        if (SteamSwitcherFuncs.SteamSettingsValid()) return true;

        foreach (var candidate in GetSteamPathCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var normalized = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                normalized = Path.GetDirectoryName(normalized) ?? "";

            if (string.IsNullOrWhiteSpace(normalized)) continue;
            if (!System.IO.File.Exists(Path.Join(normalized, "config", "loginusers.vdf"))) continue;

            SteamSettings.FolderPath = normalized;
            SteamSettings.SaveSettings();
            if (SteamSwitcherFuncs.SteamSettingsValid()) return true;
        }

        error = "Steam installation was not found. Open Steam once, then retry.";
        return false;
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
    }

    public class SteamActionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
    }
}
