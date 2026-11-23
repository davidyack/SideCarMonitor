using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SideCarMonitor.Services;

/// <summary>What Microsoft-Decision made of an event picture.</summary>
/// <param name="Subject">person, delivery, vehicle, animal or nothing.</param>
/// <param name="SubjectConfidence">Probability of the chosen subject (0..1).</param>
/// <param name="Attention">0 = ignore, 1 = FYI, 2 = look now (probability-weighted, so it can fall between).</param>
/// <param name="Package">Probability that a package is visible near a door.</param>
public sealed record EventVerdict(string Subject, double SubjectConfidence, double Attention, double Package)
{
    public string AttentionLabel => Attention >= 1.4 ? "Look now" : Attention >= 0.6 ? "FYI" : "Ignore";

    /// <summary>Short text for lists, e.g. "Delivery · Look now" or "Package at the door".</summary>
    public string Summary => Package >= 0.8 ? "Package at the door"
        : $"{char.ToUpperInvariant(Subject[0])}{Subject[1..]} · {AttentionLabel}";
}

/// <summary>
/// Asks the Microsoft-Decision model (Foundry) a few fixed questions about a camera event picture.
/// The picture goes in the "assets" form (the image_url form is read as text), at least 280 px on each side.
/// </summary>
public sealed class EventJudge(Uri endpoint, string deployment, TokenCredential credential) : IDisposable
{
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private AccessToken _token;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Builds the judge from the agent's Foundry URL (…openai.azure.com/openai/v1/ → …services.ai.azure.com).</summary>
    public static EventJudge? Create(Models.AssistantSettings a, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(a.DecisionDeployment)) return null;
        var host = a.DecisionEndpoint ?? (Uri.TryCreate(a.FoundryBaseUrl, UriKind.Absolute, out var u)
            ? $"https://{u.Host.Replace(".openai.azure.com", ".services.ai.azure.com").Replace(".cognitiveservices.azure.com", ".services.ai.azure.com")}/"
            : null);
        return host is null ? null : new EventJudge(new Uri(new Uri(host), "providers/microsoft/v1/systemone"), a.DecisionDeployment, credential);
    }

    public async Task<EventVerdict?> JudgeAsync(byte[] jpeg, string cameraName, CancellationToken ct)
    {
        var small = await ShrinkAsync(jpeg);
        var body = new JsonObject
        {
            ["model"] = deployment,
            ["state"] = $"A still from the home security camera \"{cameraName}\", taken the moment it detected activity: image:event.jpg",
            ["assets"] = new JsonObject
            {
                ["event.jpg"] = new JsonObject { ["type"] = "image", ["format"] = "jpeg", ["data_base64"] = Convert.ToBase64String(small) },
            },
            ["questions"] = new JsonObject
            {
                ["subject"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = "What is the main thing in this picture that likely triggered the camera?",
                    ["criteria"] = new JsonObject
                    {
                        ["person"] = "A person who is not obviously a delivery driver",
                        ["delivery"] = "A delivery driver, delivery van or a package being dropped off",
                        ["vehicle"] = "A car, truck or other vehicle",
                        ["animal"] = "A pet or wild animal",
                        ["nothing"] = "Nothing notable: shadows, weather, trees, lights or an empty scene",
                    },
                },
                ["attention"] = new JsonObject
                {
                    ["type"] = "score",
                    ["instructions"] = "How much should the homeowner look at this right now? Traffic passing on the road and parked cars are usually not worth attention; someone at the door, in the yard or on the driveway is.",
                    ["criteria"] = new JsonArray("Ignore", "FYI", "Look now"),
                },
                ["package"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = "Is a package or parcel visible on the porch or near a door?",
                },
            },
        };

        var json = body.ToJsonString();
        string text;
        // One request at a time; the per-minute token limit is estimated from request size, so back off on 429.
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"), // needs Content-Length
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
                using var res = await _http.SendAsync(req, ct);
                text = await res.Content.ReadAsStringAsync(ct);
                if (res.IsSuccessStatusCode) break;
                if ((int)res.StatusCode == 429 && attempt < 4) { await Task.Delay(TimeSpan.FromSeconds(20 * attempt), ct); continue; }
                throw new InvalidOperationException($"Decision model HTTP {(int)res.StatusCode}: {text[..Math.Min(200, text.Length)]}");
            }
        }
        finally
        {
            _gate.Release();
        }

        var answers = JsonNode.Parse(text)?["answers"];
        if (answers is null) return null;
        return new EventVerdict(
            Subject: answers["subject"]?["choice"]?.GetValue<string>() ?? "nothing",
            SubjectConfidence: Prob(answers["subject"], answers["subject"]?["choice"]?.GetValue<string>()),
            Attention: answers["attention"]?["score"]?.GetValue<double>() ?? 0,
            Package: answers["package"]?["noul"]?.GetValue<double>() ?? 0);
    }

    private static double Prob(JsonNode? choice, string? key) =>
        key is not null && choice?["probabilities"]?[key] is { } p ? p.GetValue<double>() : 0;

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token.ExpiresOn < DateTimeOffset.UtcNow.AddMinutes(5))
            _token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        return _token.Token;
    }

    /// <summary>
    /// Scales the camera still so the short side is 288 px (the model needs at least 280 on each side), capped at
    /// 1280 px wide, as a quality-60 JPEG. Keeps requests small; full-size stills are ~1 MB.
    /// </summary>
    private static async Task<byte[]> ShrinkAsync(byte[] jpeg)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var w = input.GetOutputStreamAt(0).AsStreamForWrite()) { await w.WriteAsync(jpeg); await w.FlushAsync(); }
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        uint width = decoder.PixelWidth, height = decoder.PixelHeight;
        var scale = Math.Min(288.0 / Math.Min(width, height), 1280.0 / width);
        scale = Math.Max(scale, 280.0 / Math.Min(width, height)); // never below the model's minimum
        if (scale >= 1) return jpeg;

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Round(width * scale),
            ScaledHeight = (uint)Math.Round(height * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);

        using var output = new InMemoryRandomAccessStream();
        var props = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.6, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, props);
        encoder.SetSoftwareBitmap(pixels);
        await encoder.FlushAsync();
        using var ms = new MemoryStream();
        using (var r = output.GetInputStreamAt(0).AsStreamForRead()) await r.CopyToAsync(ms);
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
