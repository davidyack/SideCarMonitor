using System.Net.Http.Json;
using System.Text.Json;
using SideCarMonitor.Models;

namespace SideCarMonitor.Services;

/// <summary>
/// Minimal client for the Reolink HTTP API (/cgi-bin/api.cgi).
/// Uses user/password query auth, which works on current camera and NVR firmware.
/// </summary>
public sealed class ReolinkClient : IDisposable
{
    private readonly CameraConfig _camera;
    private readonly HttpClient _http;
    private bool _aiSupported = true;

    public ReolinkClient(CameraConfig camera)
    {
        _camera = camera;
        var handler = new HttpClientHandler
        {
            // Reolink devices ship with self-signed certificates.
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        _http = new HttpClient(handler) { BaseAddress = camera.GetApiBaseUri(), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Returns the current detection flags (motion plus AI person/vehicle/animal when supported).</summary>
    public async Task<DetectionKind> GetDetectionStateAsync(CancellationToken ct)
    {
        var commands = new List<object>
        {
            new { cmd = "GetMdState", action = 0, param = new { channel = _camera.Channel } },
        };
        if (_aiSupported)
            commands.Add(new { cmd = "GetAiState", action = 0, param = new { channel = _camera.Channel } });

        var url = $"cgi-bin/api.cgi?user={Uri.EscapeDataString(_camera.Username)}&password={Uri.EscapeDataString(_camera.Password)}";
        // State polls should be quick; stills get the full 30 s because battery cameras take ~10 s to wake.
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.PostAsJsonAsync(url, commands, quick.Token);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var result = DetectionKind.None;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var cmd = item.GetProperty("cmd").GetString();
            var ok = item.TryGetProperty("code", out var code) && code.GetInt32() == 0;

            if (!ok)
            {
                if (cmd == "GetAiState")
                    _aiSupported = false; // older cameras: stop asking
                else
                    throw new InvalidOperationException(ErrorText(item));
                continue;
            }

            var value = item.GetProperty("value");
            if (cmd == "GetMdState" && value.TryGetProperty("state", out var state) && state.GetInt32() != 0)
                result |= DetectionKind.Motion;

            if (cmd == "GetAiState")
            {
                if (AlarmOn(value, "people")) result |= DetectionKind.Person;
                if (AlarmOn(value, "vehicle")) result |= DetectionKind.Vehicle;
                if (AlarmOn(value, "dog_cat")) result |= DetectionKind.Animal;
            }
        }
        return result;
    }

    /// <summary>A JPEG still from the camera (Reolink "Snap" command), or null if it fails.</summary>
    public async Task<byte[]?> GetSnapshotAsync(CancellationToken ct)
    {
        var url = $"cgi-bin/api.cgi?cmd=Snap&channel={_camera.Channel}&rs={Guid.NewGuid():N}" +
                  $"&user={Uri.EscapeDataString(_camera.Username)}&password={Uri.EscapeDataString(_camera.Password)}";
        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "image/jpeg") return null;
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static bool AlarmOn(JsonElement value, string name) =>
        value.TryGetProperty(name, out var e) &&
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty("alarm_state", out var s) &&
        s.GetInt32() != 0;

    private static string ErrorText(JsonElement item) =>
        item.TryGetProperty("error", out var err) && err.TryGetProperty("detail", out var d)
            ? $"Reolink API error: {d.GetString()}"
            : "Reolink API error";

    public void Dispose() => _http.Dispose();
}
