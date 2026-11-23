using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideCarMonitor.Models;

public sealed class AppSettings
{
    public DisplaySettings Display { get; set; } = new();
    public double MotionPollSeconds { get; set; } = 1.0;
    public int MaxEvents { get; set; } = 200;
    public WeatherSettings Weather { get; set; } = new();
    public AssistantSettings Assistant { get; set; } = new();
    public GatewaySettings Gateway { get; set; } = new();
    public List<CameraConfig> Cameras { get; set; } = [];

    public static string SettingsDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor");

    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Loads settings from %APPDATA%\SideCarMonitor\settings.json, creating it from the sample on first run.
    /// </summary>
    public static (AppSettings Settings, bool CreatedNew) Load()
    {
        Directory.CreateDirectory(SettingsDirectory);
        if (!File.Exists(SettingsPath))
        {
            var sample = Path.Combine(AppContext.BaseDirectory, "settings.sample.json");
            if (File.Exists(sample))
                File.Copy(sample, SettingsPath);
            else
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new AppSettings(), JsonOptions));
            return (Parse(File.ReadAllText(SettingsPath)), true);
        }
        return (Parse(File.ReadAllText(SettingsPath)), false);
    }

    private static AppSettings Parse(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
}

/// <summary>Voice assistant: the SideCarAgent (Copilot SDK + Foundry model router) plus Azure Speech.</summary>
public sealed class AssistantSettings
{
    /// <summary>Foundry OpenAI v1 endpoint the agent sends model calls to, e.g. https://name.openai.azure.com/openai/v1/</summary>
    public string? FoundryBaseUrl { get; set; }
    /// <summary>Deployment name for the agent's model.</summary>
    public string Model { get; set; } = "model-router";
    /// <summary>Custom-domain Speech endpoint, e.g. https://name.cognitiveservices.azure.com/</summary>
    public string? SpeechEndpoint { get; set; }
    public string TranscribeModel { get; set; } = "mai-transcribe-2-streaming";
    public string Voice { get; set; } = "en-US-Harper:MAI-Voice-2.1-Flash";
    /// <summary>Part of the microphone's name, e.g. "RODECaster Duo Main"; null = Windows default mic.</summary>
    public string? Microphone { get; set; }

    /// <summary>Microsoft-Decision deployment that labels camera event pictures; null or empty turns it off.</summary>
    public string? DecisionDeployment { get; set; } = "decision-dz";
    /// <summary>Foundry endpoint for the decision model, e.g. https://name.services.ai.azure.com/; null = derived from FoundryBaseUrl.</summary>
    public string? DecisionEndpoint { get; set; }
    /// <summary>
    /// Run the agent inside the MXC ProcessContainer sandbox (default). Set false only for development:
    /// the agent then runs with your full permissions.
    /// </summary>
    public bool Sandbox { get; set; } = true;

    /// <summary>Path to SideCarAgent.exe; null = look for the dev build next to this repo.</summary>
    public string? AgentPath { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(FoundryBaseUrl) && !string.IsNullOrWhiteSpace(SpeechEndpoint);
}

/// <summary>HTTPS API for the SideCar phone and watch apps (paired with a QR code).</summary>
public sealed class GatewaySettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8443;
}

public sealed class WeatherSettings
{
    /// <summary>Listen for a WeatherFlow Tempest hub's local UDP broadcasts.</summary>
    public bool TempestEnabled { get; set; } = true;
    /// <summary>"imperial" (F, mph, in, inHg) or "metric".</summary>
    public string Units { get; set; } = "imperial";
    public bool Imperial => !Units.Equals("metric", StringComparison.OrdinalIgnoreCase);
}

public sealed class DisplaySettings
{
    public int? MonitorIndex { get; set; }
    public bool FullScreen { get; set; } = true;

    /// <summary>Multiplier applied to every text and touch size (see UiScale.cs).
    /// 2.5 suits a ~7 inch 1080p touchscreen running at 100% Windows scaling.</summary>
    public double UiScale { get; set; } = 2.5;
}

public sealed class CameraConfig
{
    public string Name { get; set; } = "Camera";
    public string Host { get; set; } = "";
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
    public int Channel { get; set; }
    public string Stream { get; set; } = "sub";
    public int RtspPort { get; set; } = 554;
    public int HttpPort { get; set; } = 80;
    public bool UseHttps { get; set; }
    public string? RtspUrl { get; set; }

    /// <summary>"always" streams continuously; "onMotion" only streams while a detection is active
    /// (use for battery cameras so they can sleep).</summary>
    public string LiveMode { get; set; } = "always";

    public bool StreamOnMotionOnly => LiveMode.Equals("onMotion", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) || !string.IsNullOrWhiteSpace(RtspUrl);

    /// <summary>Reolink RTSP path: /h264Preview_01_sub (channel is 1-based in the URL).</summary>
    public string GetRtspUrl()
    {
        if (!string.IsNullOrWhiteSpace(RtspUrl))
            return RtspUrl;
        var user = Uri.EscapeDataString(Username);
        var pass = Uri.EscapeDataString(Password);
        var stream = Stream.Equals("main", StringComparison.OrdinalIgnoreCase) ? "main" : "sub";
        return $"rtsp://{user}:{pass}@{Host}:{RtspPort}/h264Preview_{Channel + 1:00}_{stream}";
    }

    public Uri GetApiBaseUri()
    {
        var scheme = UseHttps ? "https" : "http";
        return new Uri($"{scheme}://{Host}:{HttpPort}/");
    }
}
