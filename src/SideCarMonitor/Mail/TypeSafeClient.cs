using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SideCarMonitor.Mail
{
    /// <summary>
    /// Options for <see cref="TypeSafeClient"/>.
    /// </summary>
    public sealed class TypeSafeOptions
    {
        /// <summary>Gets or sets the API key. Falls back to the <c>TYPESAFE_API_KEY</c> environment variable.</summary>
        public string? ApiKey { get; set; }

        /// <summary>Gets or sets the API root.</summary>
        public string BaseUrl { get; set; } = "https://api.typesafe.ai";

        /// <summary>
        /// Gets or sets the model id. Pinned to a version rather than the moving <c>jev-latest</c>
        /// alias, because the thresholds in <see cref="TriageQuestions"/> are tuned against it.
        /// </summary>
        public string Model { get; set; } = "jev-1.13.0";

        /// <summary>Gets or sets the per-attempt timeout.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Gets or sets how many times a failed attempt is retried.</summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>Gets or sets the first backoff delay. It doubles on each further attempt.</summary>
        public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>Gets or sets the ceiling on a single backoff delay.</summary>
        public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// A minimal client for the TypeSafe System One API. TypeSafe publishes Python and JavaScript
    /// SDKs but no .NET one, so this wraps <c>POST /v1/systemone</c> directly.
    /// </summary>
    /// <remarks>
    /// The API returns typed answers and calibrated probabilities rather than text, so nothing here
    /// parses prose. Transient failures (408, 429 and 5xx, including 529 Overloaded) are retried
    /// with exponential backoff and jitter, honouring <c>retry-after</c>.
    /// </remarks>
    public sealed class TypeSafeClient
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly HttpClient _http;
        private readonly TypeSafeOptions _options;
        private readonly ILogger<TypeSafeClient>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="TypeSafeClient"/> class.
        /// </summary>
        /// <param name="http">The HTTP client to send requests on.</param>
        /// <param name="options">The client configuration.</param>
        /// <param name="logger">An optional logger for request summaries.</param>
        /// <exception cref="TypeSafeApiException">Thrown when no API key is configured.</exception>
        public TypeSafeClient(HttpClient http, TypeSafeOptions options, ILogger<TypeSafeClient>? logger = null)
        {
            _http = http;
            _options = options;
            _logger = logger;
            _http.Timeout = options.Timeout;
        }

        /// <summary>
        /// SideCar: send to Microsoft-Decision on Foundry (same System One API) with an Entra token per request,
        /// instead of TypeSafe with an API key. <paramref name="endpoint"/> is the full systemone URL.
        /// </summary>
        public TypeSafeClient(Uri endpoint, string model, Func<CancellationToken, Task<string>> bearer)
            : this(new HttpClient(), new TypeSafeOptions { Model = model, Timeout = TimeSpan.FromSeconds(90), MaxRetries = 4 })
        {
            _endpoint = endpoint;
            _bearer = bearer;
        }

        private readonly Uri? _endpoint;
        private readonly Func<CancellationToken, Task<string>>? _bearer;

        /// <summary>Gets the model id every request is sent with.</summary>
        public string Model => _options.Model;

        /// <summary>
        /// Evaluates every question against one state in a single request.
        /// </summary>
        /// <param name="state">The content to judge. Include only what the questions need.</param>
        /// <param name="questions">The questions, keyed by the ids the answers come back under.</param>
        /// <param name="cancellationToken">A token to cancel the request.</param>
        /// <returns>One typed answer per question id.</returns>
        /// <exception cref="TypeSafeApiException">Thrown when the API fails after all retries.</exception>
        /// <remarks>
        /// Questions are evaluated in parallel and in isolation, so batching every question about a
        /// state into one call costs only the extra input tokens and barely changes latency. Send
        /// speculative questions here rather than making a second request for them.
        /// </remarks>
        public async Task<SystemOneResponse> SystemOneAsync(
            object state,
            IReadOnlyDictionary<string, Question> questions,
            CancellationToken cancellationToken = default)
        {
            if (questions.Count == 0)
                throw new TypeSafeApiException("At least one question is required.");

            var payload = JsonSerializer.Serialize(
                new SystemOneRequest { State = state, Model = _options.Model, Questions = questions },
                SerializerOptions);

            HttpResponseMessage? response = null;
            Exception? lastError = null;
            var backoff = _options.InitialBackoff;

            for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    var delay = RetryAfter(response) ?? Jitter(backoff);
                    await Task.Delay(delay, cancellationToken);
                    backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.MaxBackoff.Ticks));
                }

                response?.Dispose();
                response = null;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint ?? new Uri("/v1/systemone", UriKind.Relative))
                    {
                        Content = new StringContent(payload, Encoding.UTF8, "application/json"),
                    };
                    if (_bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _bearer(cancellationToken));

                    response = await _http.SendAsync(request, cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        var parsed = Parse(body, RequestId(response));
                        response.Dispose();
                        response = null;

                        _logger?.LogInformation(
                            "TypeSafe {Model} answered {Count} question(s), {Tokens} input tokens, request {RequestId}.",
                            parsed.Model, parsed.Answers.Count, parsed.InputTokens, parsed.RequestId);

                        return parsed;
                    }

                    if (!IsTransient(response.StatusCode))
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        var status = (int)response.StatusCode;
                        var failedRequestId = RequestId(response);
                        response.Dispose();
                        response = null;

                        throw new TypeSafeApiException(
                            $"TypeSafe returned {status}: {Truncate(body, 500)}",
                            status,
                            failedRequestId);
                    }

                    lastError = new TypeSafeApiException(
                        $"TypeSafe returned transient {(int)response.StatusCode}.",
                        (int)response.StatusCode,
                        RequestId(response));
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex;
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastError = ex;
                }
            }

            response?.Dispose();
            throw new TypeSafeApiException(
                "TypeSafe request failed after retries.",
                (lastError as TypeSafeApiException)?.Status,
                (lastError as TypeSafeApiException)?.RequestId,
                lastError);
        }

        private static bool IsTransient(HttpStatusCode status)
            => status == HttpStatusCode.RequestTimeout
               || status == HttpStatusCode.TooManyRequests
               || (int)status >= 500;

        private static string? RequestId(HttpResponseMessage? response)
            => response is not null && response.Headers.TryGetValues("x-typesafe-request-id", out var values)
                ? values.FirstOrDefault()
                : null;

        private static TimeSpan? RetryAfter(HttpResponseMessage? response)
        {
            if (response is null)
                return null;

            if (response.Headers.TryGetValues("retry-after-ms", out var ms)
                && double.TryParse(ms.FirstOrDefault(), out var milliseconds))
            {
                return TimeSpan.FromMilliseconds(Math.Min(milliseconds, 60_000));
            }

            var delta = response.Headers.RetryAfter?.Delta;
            return delta is null ? null : TimeSpan.FromMilliseconds(Math.Min(delta.Value.TotalMilliseconds, 60_000));
        }

        private static TimeSpan Jitter(TimeSpan backoff)
            => TimeSpan.FromMilliseconds(backoff.TotalMilliseconds * (1 - (Random.Shared.NextDouble() * 0.25)));

        private static string Truncate(string value, int max)
            => value.Length <= max ? value : value[..max];

        /// <summary>
        /// Turns the response body into typed answers. Unknown answer kinds are skipped rather than
        /// failing the request, so a future primitive does not break an existing caller.
        /// </summary>
        private static SystemOneResponse Parse(string body, string? requestId)
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var answers = new Dictionary<string, Answer>(StringComparer.Ordinal);

            if (root.TryGetProperty("answers", out var answerElement)
                && answerElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in answerElement.EnumerateObject())
                {
                    var parsed = ParseAnswer(property.Value);
                    if (parsed is not null)
                        answers[property.Name] = parsed;
                }
            }

            int? inputTokens = null;
            if (root.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("input_tokens", out var tokens)
                && tokens.ValueKind == JsonValueKind.Number)
            {
                inputTokens = tokens.GetInt32();
            }

            return new SystemOneResponse
            {
                Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "unknown" : "unknown",
                Answers = answers,
                InputTokens = inputTokens,
                RequestId = requestId,
            };
        }

        private static Answer? ParseAnswer(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("type", out var typeElement))
            {
                return null;
            }

            switch (typeElement.GetString())
            {
                case "noul":
                    return new NoulAnswer { Value = Number(element, "noul") };

                case "choice":
                    return new ChoiceAnswer
                    {
                        Selected = element.TryGetProperty("choice", out var choice) ? choice.GetString() ?? string.Empty : string.Empty,
                        Confidence = Number(element, "confidence"),
                        Probabilities = StringKeyed(element, "probabilities"),
                    };

                case "score":
                    return new ScoreAnswer
                    {
                        Value = Number(element, "score"),
                        Confidence = Number(element, "confidence"),
                        Probabilities = IntKeyed(element, "probabilities"),
                    };

                default:
                    return null;
            }
        }

        private static double Number(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : 0d;

        private static IReadOnlyDictionary<string, double> StringKeyed(JsonElement element, string name)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (element.TryGetProperty(name, out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in map.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number)
                        result[property.Name] = property.Value.GetDouble();
                }
            }

            return result;
        }

        private static IReadOnlyDictionary<int, double> IntKeyed(JsonElement element, string name)
        {
            var result = new Dictionary<int, double>();
            if (element.TryGetProperty(name, out var map) && map.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in map.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number
                        && int.TryParse(property.Name, out var level))
                    {
                        result[level] = property.Value.GetDouble();
                    }
                }
            }

            return result;
        }
    }
}
