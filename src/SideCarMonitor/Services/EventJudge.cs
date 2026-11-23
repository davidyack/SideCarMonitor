using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace SideCarMonitor.Services;

/// <summary>
/// What Microsoft-Decision made of an event picture. Probabilities are 0..1.
/// Only things on our property matter: street traffic and parked cars with nobody around don't.
/// </summary>
public sealed record EventVerdict(
    double Person,          // a person on our property (driveway, yard, porch, deck, at a door)
    double AtDoor,          // someone at or approaching a door / porch
    string Vehicles,        // arriving, leaving, tampering, parked, none
    double VehiclesConfidence,
    double Animal,          // an animal in the yard
    double Package,         // a package at the door
    int Version = 0) // 0 = saved by an older version; relabeled at startup
{
    public const int CurrentVersion = 2;

    /// <summary>Why it's worth a look, most important first; empty when it isn't.</summary>
    public IReadOnlyList<string> Reasons
    {
        get
        {
            var r = new List<string>();
            if (Vehicles == "tampering" && VehiclesConfidence >= 0.4) r.Add("Someone at a car in the driveway");
            if (AtDoor >= 0.6) r.Add("Someone at the door");
            if (Vehicles == "arriving" && VehiclesConfidence >= 0.5) r.Add("Vehicle arriving");
            if (Vehicles == "leaving" && VehiclesConfidence >= 0.5) r.Add("Vehicle leaving");
            if (Person >= 0.6 && r.Count == 0) r.Add("Person on the property");
            if (Animal >= 0.6) r.Add("Animal in the yard");
            if (Package >= 0.8) r.Add("Package at the door");
            return r;
        }
    }

    public bool Alert => Reasons.Count > 0;
    public string AttentionLabel => Alert ? "Look now" : "Ignore";

    /// <summary>Short text for lists, e.g. "Someone at the door" or "Nothing on the property".</summary>
    public string Summary => Alert ? string.Join(" \u00B7 ", Reasons) : "Nothing on the property";
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

    /// <param name="watches">What the camera looks at, e.g. "the fenced dog yard behind the house".</param>
    public async Task<EventVerdict?> JudgeAsync(byte[] jpeg, string cameraName, string? watches, CancellationToken ct)
    {
        var small = await ShrinkAsync(jpeg);
        var body = new JsonObject
        {
            ["model"] = deployment,
            ["state"] = $"A still from our home security camera \"{cameraName}\"" +
                        (string.IsNullOrWhiteSpace(watches) ? "" : $", which watches {watches}") +
                        ", taken the moment it detected activity. Anything on the public street beyond our property doesn't count. image:event.jpg",
            ["assets"] = new JsonObject
            {
                ["event.jpg"] = new JsonObject { ["type"] = "image", ["format"] = "jpeg", ["data_base64"] = Convert.ToBase64String(small) },
            },
            ["questions"] = new JsonObject
            {
                ["person"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = "Is there a person on our property: our driveway, yard, porch, deck or at a door? People walking or driving by on the street don't count.",
                },
                ["at_door"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = "Is someone at, or walking up to, a door or the porch of our house?",
                },
                ["vehicles"] = new JsonObject
                {
                    ["type"] = "choice",
                    ["instructions"] = "What is happening with vehicles on OUR driveway (not the street)?",
                    ["criteria"] = new JsonObject
                    {
                        ["arriving"] = "A vehicle is pulling into our driveway",
                        ["leaving"] = "A vehicle is backing out of or leaving our driveway",
                        ["tampering"] = "A person is at, in, or touching a vehicle parked on our driveway",
                        ["parked"] = "Vehicles are parked on our driveway with nobody around them",
                        ["none"] = "No vehicle on our driveway; vehicles only on the street, or none at all",
                    },
                },
                ["animal"] = new JsonObject
                {
                    ["type"] = "noul",
                    ["instructions"] = "Is there an animal (dog, cat or wildlife) in our yard?",
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
        var vehicles = answers["vehicles"]?["choice"]?.GetValue<string>() ?? "none";
        return new EventVerdict(
            Person: Noul(answers["person"]),
            AtDoor: Noul(answers["at_door"]),
            Vehicles: vehicles,
            VehiclesConfidence: Prob(answers["vehicles"], vehicles),
            Animal: Noul(answers["animal"]),
            Package: Noul(answers["package"]),
            Version: EventVerdict.CurrentVersion);
    }

    private static double Noul(JsonNode? n) => n?["noul"]?.GetValue<double>() ?? 0;

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
