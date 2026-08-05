// Phase P data probe. A measuring instrument, not a component.
//
// Deliberately no interfaces, no repository pattern, no retry policy, no
// resilience, no abstraction over the provider, and no attempt to model the
// responses beyond what each measurement needs. It will be read once and
// deleted. Building the real client here means its findings arrive already
// baked into an abstraction chosen before the answers were known.
//
// If a call fails it prints the status code and the first 500 characters of
// the body and continues to the next measurement. Nothing retries, nothing
// throws.
//
// The token is read once into a local. It is never logged, never written to
// probe-output/, and every printed URL goes through Redact.
//
// It prints raw counts. It does not summarise, does not interpret and does not
// judge whether a result is good. Judgement happens after.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

var inv = CultureInfo.InvariantCulture;
var transcript = new StringBuilder();

void Say(string line)
{
    Console.WriteLine(line);
    transcript.AppendLine(line);
}

void Rule(string title)
{
    Say("");
    Say(new string('=', 78));
    Say(title);
    Say(new string('=', 78));
}

// ---------------------------------------------------------------- config ---

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.Secrets.json", optional: true)
    .Build();

var token = config["Eodhd:ApiToken"];
if (string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("Eodhd:ApiToken is empty or absent.");
    Console.WriteLine("Expected tools/probe/appsettings.Secrets.json beside the binary,");
    Console.WriteLine("copied from appsettings.Secrets.example.json at the repository root [D-55].");
    return 1;
}

string Redact(string s) => s.Replace(token, "***", StringComparison.Ordinal);

static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

// --------------------------------------------------- universe thresholds ---
// Not literals chosen here. Every one is a config key in CONFIG_REFERENCE.md
// set by D-4, and the $300M-$2B band is exactly the small size bucket.

const double MinMarketCap = 300_000_000;      // universe.min_market_cap
const double MidBucketFloor = 2_000_000_000;  // universe.bucket_mid_floor
const double MinPrice = 5;                    // universe.min_price
const double MinAdv20d = 2_000_000;           // universe.min_adv_20d

const int SampleSize = 6;        // six in band, per P.3

// ------------------------------------------------------------------ http ---

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
var httpRequests = 0;

// Returns null on any failure. Callers check and move to the next measurement.
async Task<JsonDocument?> Get(string path, bool quiet, params (string Key, string Value)[] query)
{
    var parts = new List<string>();
    foreach (var (k, v) in query)
    {
        parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
    }
    parts.Add("fmt=json");
    parts.Add($"api_token={Uri.EscapeDataString(token)}");

    var url = $"https://eodhd.com/api/{path}?{string.Join("&", parts)}";
    if (!quiet) Say($"  GET {Redact(url)}");

    try
    {
        httpRequests++;
        using var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            if (quiet) Say($"  GET {Redact(url)}");
            Say($"  FAILED  HTTP {(int)resp.StatusCode} {resp.StatusCode}");
            Say($"  BODY    {Redact(Truncate(body, 500))}");
            return null;
        }

        return JsonDocument.Parse(body);
    }
    catch (Exception ex)
    {
        // An unreachable host, a timeout and unparseable JSON are the same
        // class of event to a measuring instrument: print it and move on.
        if (quiet) Say($"  GET {Redact(url)}");
        Say($"  FAILED  {ex.GetType().Name}");
        Say($"  BODY    {Redact(Truncate(ex.Message, 500))}");
        return null;
    }
}

// ---------------------------------------------------- small json helpers ---

static string? Str(JsonElement e, string name) =>
    e.ValueKind == JsonValueKind.Object
    && e.TryGetProperty(name, out var v)
    && v.ValueKind == JsonValueKind.String
        ? v.GetString()
        : null;

// Null means absent or JSON null. Never zero: CLAUDE.md section 6.
static double? Num(JsonElement e, string name)
{
    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
    if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
    // Fundamentals line items arrive as decimal strings.
    if (v.ValueKind == JsonValueKind.String
        && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
    return null;
}

string ShowInt(double? d) => d is null ? "null" : ((long)d.Value).ToString("N0", inv);
string I(int n) => n.ToString("N0", inv);

// ================================================================ RUN =====

var startedUtc = DateTime.UtcNow;
var today = DateOnly.FromDateTime(startedUtc);
var todayStr = today.ToString("yyyy-MM-dd", inv);

Say("Phase P data probe");
Say($"Run date (UTC): {todayStr}");

// --------------------------------------------------------------- account ---

Rule("ACCOUNT AND QUOTA");

double? apiCallsBefore = null;
using (var doc = await Get("user", false))
{
    if (doc is null) Say("  account endpoint unavailable, continuing");
    else
    {
        var r = doc.RootElement;
        apiCallsBefore = Num(r, "apiRequests");
        Say($"  subscriptionType  {Str(r, "subscriptionType") ?? "null"}");
        Say($"  subscriptionMode  {Str(r, "subscriptionMode") ?? "null"}");
        Say($"  apiRequests       {ShowInt(apiCallsBefore)}");
        Say($"  apiRequestsDate   {Str(r, "apiRequestsDate") ?? "null"}");
        Say($"  dailyRateLimit    {ShowInt(Num(r, "dailyRateLimit"))}");
        Say($"  extraLimit        {ShowInt(Num(r, "extraLimit"))}");
    }
}

Say("");
Say("  Endpoint availability on this subscription is itself a finding: a 403 is");
Say("  a tier answer. Each measurement below prints its own status.");

// =========================================================== P.3 SAMPLE ===

Rule("P.3  SAMPLE SELECTION");

// One bulk extended call is both the selection pool and the first of the bulk
// end-of-day row counts below. If the price feed is not on the subscription it
// is neither, and a degraded symbol-list path takes over so that a 403 on one
// measurement cannot abort the others.
var pool = new List<(string Code, string Name, string Type, double Cap, double Close, double Vol50)>();
var typeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
var bulkPrimaryDate = "";
var bulkPrimaryRows = 0;
var bulkAvailable = false;

using (var doc = await Get("eod-bulk-last-day/US", false, ("filter", "extended")))
{
    if (doc is not null)
    {
        bulkAvailable = true;
        bulkPrimaryRows = doc.RootElement.GetArrayLength();
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var t = Str(row, "type") ?? "(absent)";
            typeCounts[t] = typeCounts.TryGetValue(t, out var c) ? c + 1 : 1;
            if (bulkPrimaryDate.Length == 0) bulkPrimaryDate = Str(row, "date") ?? "";

            var code = Str(row, "code");
            if (code is null) continue;
            var cap = Num(row, "MarketCapitalization");
            var close = Num(row, "adjusted_close");
            var v50 = Num(row, "avgvol_50d");
            if (cap is null || close is null || v50 is null) continue;
            pool.Add((code, Str(row, "name") ?? "", t, cap.Value, close.Value, v50.Value));
        }
    }
}

// Resolve sector and market cap for one ticker. One fundamentals call.
async Task<(string Sector, string Industry, double? Cap, string Type)> Identity(string ticker)
{
    using var doc = await Get($"fundamentals/{ticker}", true,
        ("filter", "General::Sector,General::Industry,General::Type,Highlights::MarketCapitalization"));
    if (doc is null) return ("(unavailable)", "(unavailable)", null, "(unavailable)");
    var r = doc.RootElement;
    return (Str(r, "General::Sector") ?? "(null)",
            Str(r, "General::Industry") ?? "(null)",
            Num(r, "Highlights::MarketCapitalization"),
            Str(r, "General::Type") ?? "(null)");
}

// D-4 admits common stock and ADRs. Which literal strings are used for those is
// read off the feed rather than assumed from a type name.
var admitted = new[] { "Common Stock" };

var chosen = new List<(string Ticker, string Name, string Sector, string Industry, double Cap)>();
var seenSectors = new HashSet<string>(StringComparer.Ordinal);
string controlTicker;
string controlName;
string controlSector;
double controlCap;
var priceFilterApplied = false;

if (bulkAvailable)
{
    Say($"  bulk rows {I(bulkPrimaryRows)} for date {bulkPrimaryDate}");
    Say("");
    Say("  distinct `type` values present, read off the feed rather than assumed:");
    foreach (var kv in typeCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
    {
        Say($"    {kv.Key,-22} {I(kv.Value)}");
    }

    Say("");
    Say($"  admitted `type` values: {string.Join(", ", admitted)}");
    Say("  D-4 admits common stock and ADRs. The feed carries no separate ADR type,");
    Say("  so admitting Common Stock is the closest the feed permits and ADRs are");
    Say("  not separable from it here.");

    // D-4 funnel, printed so the selection is auditable.
    var fCommon = pool.Where(p => admitted.Contains(p.Type, StringComparer.Ordinal)).ToList();
    var fCap = fCommon.Where(p => p.Cap >= MinMarketCap && p.Cap < MidBucketFloor).ToList();
    var fPrice = fCap.Where(p => p.Close >= MinPrice).ToList();
    var fVol = fPrice.Where(p => p.Vol50 * p.Close >= MinAdv20d).ToList();
    priceFilterApplied = true;

    Say("");
    Say("  D-4 universe funnel, small bucket:");
    Say($"    rows with cap, close and avgvol_50d present   {I(pool.Count)}");
    Say($"    type admitted                                 {I(fCommon.Count)}");
    Say($"    market cap in [300M, 2B)                      {I(fCap.Count)}");
    Say($"    adjusted_close >= 5                           {I(fPrice.Count)}");
    Say($"    avgvol_50d * adjusted_close >= 2M             {I(fVol.Count)}");
    Say("");
    Say("  The last line is a PROXY. D-4 asks for 20-day median dollar volume and");
    Say("  the feed offers 14, 50 and 200-day average share volume and no median.");
    Say("  Average volume is unadjusted while adjusted_close is adjusted, so the");
    Say("  product understates dollar volume for any name that split inside the");
    Say("  window. Adequate for picking six sample names, not for the phase 1");
    Say("  universe filter, which must compute the metric from the price series.");

    // Deterministic: market cap descending, then code ascending.
    fVol.Sort((a, b) => b.Cap != a.Cap ? b.Cap.CompareTo(a.Cap) : string.CompareOrdinal(a.Code, b.Code));

    // Walk six equal strata of the band so the sample spans it, and inside each
    // stratum take the first name whose sector is not already held.
    var stratum = Math.Max(1, fVol.Count / SampleSize);
    Say("");
    Say("  resolving sectors (one fundamentals call per candidate):");

    for (var s = 0; s < SampleSize && chosen.Count < SampleSize; s++)
    {
        for (var attempt = 0; attempt < 8 && s * stratum + attempt < fVol.Count; attempt++)
        {
            var cand = fVol[s * stratum + attempt];
            if (chosen.Any(c => c.Ticker == cand.Code + ".US")) continue;

            var (sector, industry, fcap, _) = await Identity(cand.Code + ".US");
            Say($"    {cand.Code,-8} {sector,-24} bulk cap {ShowInt(cand.Cap),16}"
                + (fcap is null ? "" : $"   fundamentals cap {ShowInt(fcap)}"));

            if (sector is "(unavailable)" or "(null)") continue;
            if (seenSectors.Contains(sector) && chosen.Count < SampleSize - 1) continue;

            seenSectors.Add(sector);
            chosen.Add((cand.Code + ".US", cand.Name, sector, industry, cand.Cap));
            break;
        }
    }

    var ctl = pool.Where(p => admitted.Contains(p.Type, StringComparer.Ordinal))
                  .OrderByDescending(p => p.Cap).ThenBy(p => p.Code, StringComparer.Ordinal)
                  .First();
    var (cs, _, _, _) = await Identity(ctl.Code + ".US");
    controlTicker = ctl.Code + ".US";
    controlName = ctl.Name;
    controlSector = cs;
    controlCap = ctl.Cap;
}
else
{
    // Degraded path. The exchange symbol list carries identity only, so market
    // cap and sector both come from one fundamentals call per candidate, and the
    // D-4 price and volume floors cannot be applied at all.
    Say("");
    Say("  DEGRADED SELECTION. The price feed is not on this subscription, so:");
    Say("    - the bulk end-of-day row count cannot be measured at all");
    Say("    - universe.min_price and universe.min_adv_20d cannot be applied");
    Say("    - the sample is filtered on type and market cap only");
    Say("  The names below are therefore in the cap band but not verified as");
    Say("  members of the D-4 universe.");

    var symbols = new List<(string Code, string Name, string Type)>();
    using (var doc = await Get("exchange-symbol-list/US", false))
    {
        if (doc is null)
        {
            Say("  exchange symbol list also unavailable. No sample can be selected.");
        }
        else
        {
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                var t = Str(row, "Type") ?? "(absent)";
                typeCounts[t] = typeCounts.TryGetValue(t, out var c) ? c + 1 : 1;
                var code = Str(row, "Code");
                if (code is null) continue;
                symbols.Add((code, Str(row, "Name") ?? "", t));
            }
        }
    }

    Say("");
    Say("  distinct `Type` values present, read off the feed rather than assumed:");
    foreach (var kv in typeCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal))
    {
        Say($"    {kv.Key,-22} {I(kv.Value)}");
    }
    Say("");
    Say($"  admitted `Type` values: {string.Join(", ", admitted)}");

    var candidates = symbols.Where(s => admitted.Contains(s.Type, StringComparer.Ordinal))
                            .OrderBy(s => s.Code, StringComparer.Ordinal)
                            .ToList();
    Say($"  admitted symbols: {I(candidates.Count)}");
    Say("");
    Say("  walking the admitted list at a fixed stride, one fundamentals call each,");
    Say("  accepting the first name in the band whose sector is not already held:");

    var stride = Math.Max(1, candidates.Count / 400);
    var best = ("", "", 0d);
    var looked = 0;

    for (var i = 0; i < candidates.Count && chosen.Count < SampleSize; i += stride)
    {
        var cand = candidates[i];
        var (sector, industry, fcap, ftype) = await Identity(cand.Code + ".US");
        looked++;
        if (fcap is null || sector is "(unavailable)" or "(null)") continue;
        if (fcap.Value > best.Item3) best = (cand.Code + ".US", sector, fcap.Value);
        if (fcap.Value < MinMarketCap || fcap.Value >= MidBucketFloor) continue;
        if (!admitted.Contains(ftype, StringComparer.Ordinal)) continue;
        if (seenSectors.Contains(sector) && chosen.Count < SampleSize - 1) continue;

        Say($"    {cand.Code,-8} {sector,-24} cap {ShowInt(fcap)}   ACCEPTED");
        seenSectors.Add(sector);
        chosen.Add((cand.Code + ".US", cand.Name, sector, industry, fcap.Value));
    }

    Say($"  candidates looked up: {I(looked)}");
    controlTicker = best.Item1;
    controlName = "(largest found in the sampled walk)";
    controlSector = best.Item2;
    controlCap = best.Item3;
    Say("");
    Say("  CONTROL CAVEAT: without the bulk feed the whole market cannot be ranked,");
    Say("  so the control below is the largest name found in the sampled walk and is");
    Say("  not verified as the market maximum.");
}

Say("");
Say("  SELECTED SET");
Say($"    {"ticker",-10} {"market cap",16}  {"sector",-26} name");
foreach (var c in chosen)
{
    Say($"    {c.Ticker,-10} {ShowInt(c.Cap),16}  {c.Sector,-26} {Truncate(c.Name, 32)}");
}
if (controlTicker.Length > 0)
{
    Say($"    {controlTicker,-10} {ShowInt(controlCap),16}  {controlSector,-26} {Truncate(controlName, 32)}   <- CONTROL");
}
Say("");
Say($"  in band: {I(chosen.Count)}   distinct sectors: {I(seenSectors.Count)}"
    + $"   (P.3 asks for 6 across 4 or more)");
Say($"  D-4 price and volume floors applied: {(priceFilterApplied ? "yes" : "NO, price feed unavailable")}");

var tickers = chosen.Select(c => c.Ticker).ToList();
if (controlTicker.Length > 0) tickers.Add(controlTicker);

// ----------------------------------------------------------------- close ---

Rule("RUN SUMMARY");
Say($"  http requests issued  {I(httpRequests)}");

using (var doc = await Get("user", true))
{
    if (doc is not null)
    {
        var after = Num(doc.RootElement, "apiRequests");
        Say($"  weighted api calls    {ShowInt(apiCallsBefore)} -> {ShowInt(after)}"
            + (apiCallsBefore is not null && after is not null
                ? $"   consumed {ShowInt(after - apiCallsBefore)}" : ""));
        Say($"  dailyRateLimit        {ShowInt(Num(doc.RootElement, "dailyRateLimit"))}");
    }
}

var outDir = Path.Combine(AppContext.BaseDirectory, "probe-output");
Directory.CreateDirectory(outDir);
var outPath = Path.Combine(outDir, $"probe-{startedUtc.ToString("yyyyMMdd-HHmmss", inv)}.txt");
await File.WriteAllTextAsync(outPath, Redact(transcript.ToString()));
Console.WriteLine($"\ntranscript written to {outPath}");

return 0;
