using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SideCarMonitor.Services;

/// <summary>
/// Flight search for the agent's flight_search tool, through Duffel (airline content incl. United; the token is in
/// %APPDATA%\SideCarMonitor\duffel-token.txt). Read-only: searches offers and seat maps; it never books or pays.
/// For each day (or day pair) in the ranges it asks Duffel for offers, keeps the requested airline, ranks them
/// (fewest stops, then price, then duration), and for the best few reads the seat map to find available exit-row
/// seats that recline: an exit row directly in front of another exit row doesn't recline (e.g. United 737 row 20),
/// the rear one does (row 21).
/// </summary>
public sealed class FlightSearch
{
    private static readonly string TokenPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SideCarMonitor", "duffel-token.txt");
    private readonly HttpClient _http;
    public bool TestMode { get; }

    private FlightSearch(string token)
    {
        TestMode = token.StartsWith("duffel_test_");
        _http = new HttpClient { BaseAddress = new Uri("https://api.duffel.com/"), Timeout = TimeSpan.FromSeconds(90) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Add("Duffel-Version", "v2");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static FlightSearch? Create()
    {
        if (!File.Exists(TokenPath)) return null;
        var t = File.ReadAllText(TokenPath).Trim();
        return t.Length == 0 ? null : new FlightSearch(t);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = new StringContent(new JsonObject { ["data"] = body }.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            var err = JsonNode.Parse(text.StartsWith('{') ? text : "{}")?["errors"]?[0];
            throw new InvalidOperationException($"Duffel {(int)res.StatusCode}: {err?["message"] ?? err?["title"] ?? text[..Math.Min(160, text.Length)]}");
        }
        return JsonNode.Parse(text);
    }

    public sealed record SeatPick(string Segment, string Flight, List<string> RecliningExit, List<string> OtherExit, string Price);
    public sealed record Option(string Summary, decimal Total, string Currency, int Stops, TimeSpan Duration, List<SeatPick> Seats, string OfferId);

    /// <summary>Searches each date (or date pair) and returns the best options with exit-row seat details.</summary>
    public async Task<object> SearchAsync(string from, string to, DateOnly departFrom, DateOnly departTo, DateOnly? returnFrom, DateOnly? returnTo,
        string airline, bool nonstopOnly, int maxResults = 5)
    {
        var departs = Days(departFrom, departTo, 4);
        var returns = returnFrom is null ? [] : Days(returnFrom.Value, returnTo ?? returnFrom.Value, 3);
        var pairs = returns.Count == 0 ? departs.Select(d => (d, (DateOnly?)null)).ToList()
                  : departs.SelectMany(d => returns.Where(r => r >= d).Select(r => (d, (DateOnly?)r))).Take(9).ToList();

        // Offer requests in parallel (3 at a time); each takes a few seconds.
        var offers = new List<JsonNode>();
        var gate = new SemaphoreSlim(3);
        var errors = new List<string>();
        await Task.WhenAll(pairs.Select(async pair =>
        {
            await gate.WaitAsync();
            try
            {
                var slices = new JsonArray(new JsonObject { ["origin"] = from, ["destination"] = to, ["departure_date"] = pair.Item1.ToString("yyyy-MM-dd") });
                if (pair.Item2 is { } r) slices.Add(new JsonObject { ["origin"] = to, ["destination"] = from, ["departure_date"] = r.ToString("yyyy-MM-dd") });
                var body = new JsonObject
                {
                    ["slices"] = slices, ["passengers"] = new JsonArray(new JsonObject { ["type"] = "adult" }), ["cabin_class"] = "economy",
                };
                if (nonstopOnly) body["max_connections"] = 0;
                var res = await SendAsync(HttpMethod.Post, "air/offer_requests?return_offers=true&supplier_timeout=20000", body);
                lock (offers) offers.AddRange((res?["data"]?["offers"]?.AsArray() ?? []).Where(o => o is not null)!);
            }
            catch (Exception ex) { lock (errors) errors.Add($"{pair.Item1:MMM d}: {ex.Message}"); }
            finally { gate.Release(); }
        }));

        // Keep the airline asked for (in test mode Duffel only has its fake "Duffel Airways", ZZ).
        var code = TestMode ? "ZZ" : airline.ToUpperInvariant();
        var mine = offers.Where(o => code.Length == 0 || o["owner"]?["iata_code"]?.GetValue<string>() == code).ToList();
        var ranked = mine.Select(o => (Offer: o, Stops: Stops(o), Total: decimal.Parse(o["total_amount"]!.GetValue<string>(), CultureInfo.InvariantCulture), Dur: Duration(o)))
            .OrderBy(x => x.Stops).ThenBy(x => x.Total).ThenBy(x => x.Dur).ToList();
        // One per itinerary (the same flights at several fares): keep the cheapest.
        var distinct = ranked.GroupBy(x => Flights(x.Offer)).Select(g => g.First()).Take(maxResults).ToList();

        var options = new List<Option>();
        foreach (var x in distinct)
        {
            var seats = await ExitSeatsAsync(x.Offer);
            options.Add(new Option(Describe(x.Offer), x.Total, x.Offer["total_currency"]?.GetValue<string>() ?? "USD", x.Stops, x.Dur, seats, x.Offer["id"]!.GetValue<string>()));
        }

        return new
        {
            mode = TestMode ? "TEST MODE: Duffel's sample airline only, not real United flights. Say so to the user." : "live",
            searched = pairs.Select(p => p.Item2 is { } r ? $"{p.Item1:ddd MMM d} / {r:ddd MMM d}" : $"{p.Item1:ddd MMM d}"),
            offersFound = offers.Count,
            matchingAirline = mine.Count,
            errors,
            options = options.Select(o => new
            {
                itinerary = o.Summary, price = $"{o.Total:0} {o.Currency}", stops = o.Stops, duration = $"{(int)o.Duration.TotalHours}h {o.Duration.Minutes}m",
                exitRowReclining = o.Seats.Select(s => $"{s.Flight} {s.Segment}: " + (s.RecliningExit.Count > 0 ? string.Join(", ", s.RecliningExit) + (s.Price.Length > 0 ? $" ({s.Price})" : "") : "none open")),
                exitRowNoRecline = o.Seats.Where(s => s.OtherExit.Count > 0).Select(s => $"{s.Flight}: {string.Join(", ", s.OtherExit)}"),
            }),
            bookOn = airline.Equals("UA", StringComparison.OrdinalIgnoreCase)
                ? $"https://www.united.com/en/us/fsr/choose-flights?f={from}&t={to}&d={departFrom:yyyy-MM-dd}" + (returnFrom is { } rf ? $"&r={rf:yyyy-MM-dd}&tt=0" : "&tt=1") + "&px=1&taxng=1&clm=7"
                : null,
            note = "Search only: nothing was booked. Exit-row availability and prices can change; confirm on the airline's site.",
        };
    }

    private static List<DateOnly> Days(DateOnly a, DateOnly b, int max)
    {
        if (b < a) (a, b) = (b, a);
        var all = new List<DateOnly>();
        for (var d = a; d <= b; d = d.AddDays(1)) all.Add(d);
        if (all.Count <= max) return all;
        return Enumerable.Range(0, max).Select(i => all[(int)Math.Round(i * (all.Count - 1) / (double)(max - 1))]).Distinct().ToList();
    }

    private static IEnumerable<JsonNode> Segments(JsonNode offer) =>
        (offer["slices"]?.AsArray() ?? []).SelectMany(s => s?["segments"]?.AsArray() ?? []).Where(s => s is not null)!;

    private static int Stops(JsonNode offer) => (offer["slices"]?.AsArray() ?? []).Max(s => (s?["segments"]?.AsArray().Count ?? 1) - 1);
    private static TimeSpan Duration(JsonNode offer) => (offer["slices"]?.AsArray() ?? []).Aggregate(TimeSpan.Zero, (t, s) =>
        t + (System.Xml.XmlConvert.ToTimeSpan(s?["duration"]?.GetValue<string>() ?? "PT0M")));
    private static string Flights(JsonNode offer) => string.Join(",", Segments(offer).Select(FlightNo));
    private static string FlightNo(JsonNode seg) =>
        (seg["marketing_carrier"]?["iata_code"]?.GetValue<string>() ?? "") + (seg["marketing_carrier_flight_number"]?.GetValue<string>() ?? "");

    private static string Describe(JsonNode offer) => string.Join("  |  ", (offer["slices"]?.AsArray() ?? []).Select(sl =>
    {
        var segs = sl?["segments"]?.AsArray().Where(s => s is not null).ToList() ?? [];
        if (segs.Count == 0) return "";
        DateTime T(JsonNode? n) => DateTime.Parse(n!.GetValue<string>(), CultureInfo.InvariantCulture);
        var first = segs[0]!; var last = segs[^1]!;
        var route = string.Join("-", segs.Select(s => s!["origin"]?["iata_code"]?.GetValue<string>()).Append(last["destination"]?["iata_code"]?.GetValue<string>()));
        var planes = string.Join("/", segs.Select(s => s!["aircraft"]?["name"]?.GetValue<string>() ?? "?").Distinct());
        return $"{T(first["departing_at"]):ddd MMM d h:mm tt} {route} arr {T(last["arriving_at"]):h:mm tt} " +
               $"({string.Join("+", segs.Select(s => FlightNo(s!)))}, {planes})";
    }));

    /// <summary>Available exit-row seats per segment: reclining (rear exit row) vs not (front of a double exit row).</summary>
    private async Task<List<SeatPick>> ExitSeatsAsync(JsonNode offer)
    {
        var picks = new List<SeatPick>();
        JsonNode? maps;
        try { maps = await SendAsync(HttpMethod.Get, $"air/seat_maps?offer_id={offer["id"]!.GetValue<string>()}"); }
        catch { return picks; } // seat maps aren't available for every flight
        var segById = Segments(offer).ToDictionary(s => s["id"]!.GetValue<string>(), s => s);
        foreach (var map in maps?["data"]?.AsArray() ?? [])
        {
            if (map is null) continue;
            segById.TryGetValue(map["segment_id"]?.GetValue<string>() ?? "", out var seg);
            var label = seg is null ? "" : $"{seg["origin"]?["iata_code"]}-{seg["destination"]?["iata_code"]}";
            var reclining = new List<string>(); var other = new List<string>(); var prices = new List<string>();
            foreach (var cabin in map["cabins"]?.AsArray() ?? [])
            {
                var rows = cabin?["rows"]?.AsArray().Where(r => r is not null).ToList() ?? [];
                bool IsExitRow(int i) => i >= 0 && i < rows.Count &&
                    (rows[i]!["sections"]?.AsArray() ?? []).SelectMany(s => s?["elements"]?.AsArray() ?? []).Any(e => e?["type"]?.GetValue<string>() == "exit_row");
                bool HasSeats(int i) => (rows[i]!["sections"]?.AsArray() ?? []).SelectMany(s => s?["elements"]?.AsArray() ?? []).Any(e => e?["type"]?.GetValue<string>() == "seat");
                for (var i = 0; i < rows.Count; i++)
                {
                    if (!HasSeats(i) || !IsExitRow(i - 1)) continue; // a seat row right behind an exit = exit-row legroom
                    var nextSeatRow = Enumerable.Range(i + 1, Math.Max(0, rows.Count - i - 1)).FirstOrDefault(j => HasSeats(j) || IsExitRow(j), -1);
                    var reclines = !(nextSeatRow >= 0 && IsExitRow(nextSeatRow)); // another exit right behind = no recline
                    foreach (var e in (rows[i]!["sections"]?.AsArray() ?? []).SelectMany(s => s?["elements"]?.AsArray() ?? []))
                    {
                        if (e?["type"]?.GetValue<string>() != "seat") continue;
                        var services = e["available_services"]?.AsArray() ?? [];
                        if (services.Count == 0) continue; // taken or not selectable
                        var disclosures = string.Join(" ", (e["disclosures"]?.AsArray() ?? []).Select(d => d?.GetValue<string>()));
                        var seatReclines = reclines && !disclosures.Contains("recline", StringComparison.OrdinalIgnoreCase);
                        (seatReclines ? reclining : other).Add(e["designator"]?.GetValue<string>() ?? "?");
                        var amt = services[0]?["total_amount"]?.GetValue<string>();
                        if (seatReclines && amt is not null) prices.Add($"{decimal.Parse(amt, CultureInfo.InvariantCulture):0} {services[0]?["total_currency"]}");
                    }
                }
            }
            picks.Add(new SeatPick(label, seg is null ? "" : FlightNo(seg), reclining, other,
                prices.Count == 0 ? "" : prices.Distinct().Count() == 1 ? prices[0] : $"from {prices.Min()}"));
        }
        return picks;
    }
}
