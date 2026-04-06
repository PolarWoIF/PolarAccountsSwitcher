using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PolarWolves_Globals;

namespace PolarWolves_Server.Controllers;

[ApiController]
[Route("api/ui/settings")]
public class UiSettingsController : ControllerBase
{
    private static readonly object FileLock = new();
    private static readonly List<string> DefaultPlatformOrder =
    [
        "steam",
        "ubisoft",
        "epic",
        "ea",
        "rockstar",
        "riot",
        "gog",
        "battlenet"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string SettingsFilePath => Path.Join(Globals.UserDataFolder, "zenith-ui-settings.json");

    [HttpGet]
    public IActionResult Get()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsFilePath))
            {
                return Ok(new UiSettingsResponse
                {
                    Success = true,
                    Message = "default",
                    PlatformOrder = new List<string>(DefaultPlatformOrder),
                    EnabledPlatformIds = new List<string>(DefaultPlatformOrder)
                });
            }

            UiSettingsBody? model;
            lock (FileLock)
            {
                var raw = System.IO.File.ReadAllText(SettingsFilePath);
                model = JsonSerializer.Deserialize<UiSettingsBody>(raw, JsonOptions);
            }

            model ??= new UiSettingsBody();

            return Ok(new UiSettingsResponse
            {
                Success = true,
                Message = "ok",
                PlatformOrder = NormalizeOrder(model.PlatformOrder),
                EnabledPlatformIds = NormalizeEnabled(model.EnabledPlatformIds)
            });
        }
        catch (Exception ex)
        {
            return Ok(new UiSettingsResponse
            {
                Success = false,
                Message = ex.Message,
                PlatformOrder = new List<string>(),
                EnabledPlatformIds = new List<string>()
            });
        }
    }

    [HttpPost]
    public IActionResult Save([FromBody] UiSettingsBody request)
    {
        request ??= new UiSettingsBody();
        request.PlatformOrder = NormalizeOrder(request.PlatformOrder);
        request.EnabledPlatformIds = NormalizeEnabled(request.EnabledPlatformIds);

        try
        {
            var folder = Path.GetDirectoryName(SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            lock (FileLock)
            {
                var json = JsonSerializer.Serialize(request, JsonOptions);
                System.IO.File.WriteAllText(SettingsFilePath, json);
            }

            return Ok(new UiSettingsResponse
            {
                Success = true,
                Message = "saved",
                PlatformOrder = request.PlatformOrder,
                EnabledPlatformIds = request.EnabledPlatformIds
            });
        }
        catch (Exception ex)
        {
            return Ok(new UiSettingsResponse
            {
                Success = false,
                Message = ex.Message,
                PlatformOrder = request.PlatformOrder,
                EnabledPlatformIds = request.EnabledPlatformIds
            });
        }
    }

    public class UiSettingsBody
    {
        public List<string> PlatformOrder { get; set; } = new();
        public List<string> EnabledPlatformIds { get; set; } = new();
    }

    public class UiSettingsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public List<string> PlatformOrder { get; set; } = new();
        public List<string> EnabledPlatformIds { get; set; } = new();
    }

    private static List<string> NormalizeOrder(List<string>? input)
    {
        var source = input ?? new List<string>();
        var cleaned = source
            .Where(id => !string.IsNullOrWhiteSpace(id) && DefaultPlatformOrder.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cleaned.Count == 0) return new List<string>(DefaultPlatformOrder);

        var missing = DefaultPlatformOrder
            .Where(id => !cleaned.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        cleaned.AddRange(missing);
        return cleaned;
    }

    private static List<string> NormalizeEnabled(List<string>? input)
    {
        var source = input ?? new List<string>();
        var cleaned = source
            .Where(id => !string.IsNullOrWhiteSpace(id) && DefaultPlatformOrder.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cleaned.Count == 0 ? new List<string>(DefaultPlatformOrder) : cleaned;
    }
}
