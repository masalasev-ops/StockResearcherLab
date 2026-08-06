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
const int BacklookYears = 5;     // D-47, five years of backfill
const int NewsPageCap = 20;      // 20 x 1000 articles per window. Printed when it binds.

// fundamentals.min_clean_gaps_for_substitution, set by D-62. Below this a ticker
// has no view of its own filing behaviour and is excluded rather than guessed at.
const int MinCleanGapsForSubstitution = 4;

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

string Show(double? d) => d is null ? "null" : d.Value.ToString("0.######", inv);
string ShowInt(double? d) => d is null ? "null" : ((long)d.Value).ToString("N0", inv);
string I(int n) => n.ToString("N0", inv);

// ================================================================ RUN =====

var startedUtc = DateTime.UtcNow;
var today = DateOnly.FromDateTime(startedUtc);
var todayStr = today.ToString("yyyy-MM-dd", inv);

// H.4 re-run mode: `probe filing-dates TICKER,TICKER,...` runs P.4.4 alone over
// an explicit list. The sample was already selected and recorded, so this
// re-measures the named names rather than reselecting a new set, and it costs a
// handful of calls rather than the two thousand a full run weighs.
if (args.Length >= 2 && string.Equals(args[0], "filing-dates", StringComparison.Ordinal))
{
    var only = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Say("Phase P data probe, H.4 re-run");
    Say($"Run date (UTC): {todayStr}");
    Say($"P.4.4 only, over an explicit list of {I(only.Length)}: {string.Join(", ", only)}");
    await FilingDates(only);
    Rule("RUN SUMMARY");
    Say($"  http requests issued  {I(httpRequests)}");
    Console.WriteLine($"\ntranscript written to {SaveTranscript("probe-filing-dates")}");
    return 0;
}

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

// ===================================================== BULK EOD ROW COUNT ==
// Runs before selection, because the count decides which day the selection
// pool is drawn from. The last available day is routinely still accreting
// rows, and a pool drawn from a part-settled day is biased toward names that
// happened to have printed a trade by the time the probe ran.

Rule("BULK END-OF-DAY ROW COUNT");
Say("  Five consecutive recent US trading days. One day gives a level; the");
Say("  freshness guard needs a band, and freshness.row_count_tolerance has no");
Say("  default until this measures one.");
Say("");
Say($"    {"requested",-12} {"payload date",-13} rows");

var bulkDays = new List<(string Date, int Rows)>();
var bulkAvailable = false;
var lastAvailable = today;

using (var doc = await Get("eod-bulk-last-day/US", false))
{
    if (doc is not null)
    {
        bulkAvailable = true;
        var n = doc.RootElement.GetArrayLength();
        var d = n > 0 ? Str(doc.RootElement[0], "date") ?? "" : "";
        if (DateOnly.TryParse(d, inv, out var parsed)) lastAvailable = parsed;
        bulkDays.Add((d, n));
        Say($"    {"(last)",-12} {d,-13} {I(n)}");
    }
}

var walked = 0;
var stepBack = 1;
while (bulkAvailable && walked < 4 && stepBack < 15)
{
    var d = lastAvailable.AddDays(-stepBack);
    stepBack++;
    if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;

    using var doc = await Get("eod-bulk-last-day/US", true, ("date", d.ToString("yyyy-MM-dd", inv)));
    if (doc is null) { Say($"    {d.ToString("yyyy-MM-dd", inv),-12} {"(failed)",-13} -"); walked++; continue; }
    var n = doc.RootElement.GetArrayLength();
    var payloadDate = n > 0 ? Str(doc.RootElement[0], "date") ?? "" : "(empty)";
    bulkDays.Add((payloadDate, n));
    Say($"    {d.ToString("yyyy-MM-dd", inv),-12} {payloadDate,-13} {I(n)}");
    walked++;
}

if (!bulkAvailable)
{
    Say("    NOT MEASURED. The bulk end-of-day endpoint returns 403 on this");
    Say("    subscription, so no row count exists and freshness.row_count_tolerance");
    Say("    keeps its 'from probe' placeholder.");
}

// The selection pool is drawn from the most recent day that looks settled,
// meaning within 10 percent of the largest count seen.
var maxRows = bulkDays.Count == 0 ? 0 : bulkDays.Max(b => b.Rows);
var settled = bulkDays.Where(b => b.Rows >= maxRows * 0.9 && b.Date.Length == 10)
                      .OrderByDescending(b => b.Date, StringComparer.Ordinal)
                      .ToList();
var selectionDate = settled.Count > 0 ? settled[0].Date : "";

if (bulkDays.Count > 0)
{
    Say("");
    Say($"  spread across the settled days: {I(bulkDays.Where(b => b.Rows >= maxRows * 0.9).Min(b => b.Rows))}"
        + $" to {I(maxRows)}");
    Say($"  most recent day still accreting: {(bulkDays[0].Rows < maxRows * 0.9 ? "yes, " + I(bulkDays[0].Rows) + " rows" : "no")}");
    Say($"  selection pool will be drawn from {(selectionDate.Length > 0 ? selectionDate : "(none settled)")}");
}

// =========================================================== P.3 SAMPLE ===

Rule("P.3  SAMPLE SELECTION");

// One bulk extended call on the settled day chosen above. If the price feed is
// not on the subscription a degraded symbol-list path takes over, so that a 403
// on one measurement cannot abort the others.
var pool = new List<(string Code, string Name, string Type, double Cap, double Close, double Vol50)>();
var typeCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
var bulkPrimaryDate = "";
var bulkPrimaryRows = 0;

using (var doc = selectionDate.Length > 0
    ? await Get("eod-bulk-last-day/US", false, ("filter", "extended"), ("date", selectionDate))
    : await Get("eod-bulk-last-day/US", false, ("filter", "extended")))
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

// Calendar-year windows covering the five-year backfill window [D-47].
var fromFive = today.AddYears(-BacklookYears);
var years = new List<(int Year, DateOnly From, DateOnly To, bool Partial)>();
for (var y = fromFive.Year; y <= today.Year; y++)
{
    var f = y == fromFive.Year ? fromFive : new DateOnly(y, 1, 1);
    var t = y == today.Year ? today : new DateOnly(y, 12, 31);
    years.Add((y, f, t, y == fromFive.Year || y == today.Year));
}

// ============================================================ P.4.1 NEWS ==

Rule("P.4.1  NEWS ARCHIVE DEPTH");
Say($"  Five-year sweep from {fromFive.ToString("yyyy-MM-dd", inv)}, paged at limit=1000 per calendar year.");
Say("  Digests are forward-only, so the 90-day density at the foot of each block");
Say("  is the figure a decision rests on. s5.news_gate_min_articles is 3 articles");
Say("  in 7 days, which is the scale that gate operates at.");

foreach (var ticker in tickers)
{
    Say("");
    Say($"  {ticker}");
    string? earliest = null;
    var total = 0;
    var capBound = false;

    foreach (var (year, wFrom, wTo, partial) in years)
    {
        var count = 0;
        var page = 0;
        while (page < NewsPageCap)
        {
            using var doc = await Get("news", true,
                ("s", ticker),
                ("from", wFrom.ToString("yyyy-MM-dd", inv)),
                ("to", wTo.ToString("yyyy-MM-dd", inv)),
                ("limit", "1000"),
                ("offset", (page * 1000).ToString(inv)));
            if (doc is null) break;

            var n = doc.RootElement.GetArrayLength();
            count += n;
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                var d = Str(a, "date");
                if (d is null) continue;
                var day = d.Length >= 10 ? d[..10] : d;
                if (earliest is null || string.CompareOrdinal(day, earliest) < 0) earliest = day;
            }
            page++;
            if (n < 1000) break;
            if (page == NewsPageCap) capBound = true;
        }
        total += count;
        Say($"    {year.ToString(inv)}{(partial ? " (partial)" : "         ")}  articles {I(count)}"
            + (capBound ? "   PAGE CAP BOUND, count is a floor not a total" : ""));
        capBound = false;
    }

    var d90 = 0;
    using (var doc = await Get("news", true,
        ("s", ticker),
        ("from", today.AddDays(-90).ToString("yyyy-MM-dd", inv)),
        ("to", todayStr),
        ("limit", "1000")))
    {
        if (doc is not null) d90 = doc.RootElement.GetArrayLength();
    }

    Say($"    earliest article date  {earliest ?? "none returned"}");
    Say($"    total over five years  {I(total)}");
    Say($"    last 90 days           {I(d90)}   <- forward-only digest density");
}

// ======================================================= P.4.2 SENTIMENT ==

Rule("P.4.2  SENTIMENT DEPTH AND COVERAGE");
Say("  Five years windowed by calendar year, because S3 ranks on an article count");
Say("  z-scored against the ticker's own 90-day baseline and D-47 backfills five");
Say("  years. A series shorter than that cannot be backfilled, so its floor has no");
Say("  trailing distribution and the screen is inert at go-live.");
Say("  Days with no news are omitted from the response, so rows returned is the");
Say("  coverage count and a row carrying count 0 is the distinct empty case.");

foreach (var ticker in tickers)
{
    Say("");
    Say($"  {ticker}");
    string? earliest = null;
    var total = 0;

    foreach (var (year, wFrom, wTo, partial) in years)
    {
        var rows = 0;
        using var doc = await Get("sentiments", true,
            ("s", ticker),
            ("from", wFrom.ToString("yyyy-MM-dd", inv)),
            ("to", wTo.ToString("yyyy-MM-dd", inv)));
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                rows += prop.Value.GetArrayLength();
                foreach (var r in prop.Value.EnumerateArray())
                {
                    var d = Str(r, "date");
                    if (d is null) continue;
                    if (earliest is null || string.CompareOrdinal(d, earliest) < 0) earliest = d;
                }
            }
        }
        total += rows;
        Say($"    {year.ToString(inv)}{(partial ? " (partial)" : "         ")}  rows {I(rows)}");
    }

    // The 180-day detail the prompt asks for, reported separately.
    int rows180 = 0, nonZero = 0, maxCount = 0;
    double sum = 0;
    using (var doc = await Get("sentiments", true,
        ("s", ticker),
        ("from", today.AddDays(-180).ToString("yyyy-MM-dd", inv)),
        ("to", todayStr)))
    {
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var r in prop.Value.EnumerateArray())
                {
                    rows180++;
                    var c = Num(r, "count");
                    if (c is null) continue;
                    if (c.Value > 0) nonZero++;
                    sum += c.Value;
                    if (c.Value > maxCount) maxCount = (int)c.Value;
                }
            }
        }
    }

    Say($"    earliest date with a row   {earliest ?? "none returned"}");
    Say($"    total rows over five years {I(total)}");
    Say($"    last 180 days: days with a row {I(rows180)}, days with non-zero count {I(nonZero)}, "
        + $"mean count {(rows180 == 0 ? "n/a" : (sum / rows180).ToString("0.##", inv))}, max count {I(maxCount)}");
}

// ============================================================ P.4.3 FLOW ==

Rule("P.4.3  FLOW COVERAGE");
Say("  S4 ranks on insider_net_90d_usd, distinct_buyer_count, short_interest_change");
Say("  and inst_ownership_change. All four are measured here.");

foreach (var ticker in tickers)
{
    Say("");
    Say($"  {ticker}");

    // --- insider transactions, last 90 days -------------------------------
    // Two endpoints, both measured, because they do not agree. Over the same
    // seven names and the same 90 days the legacy flat endpoint returned 0
    // transactions for every one of them including the megacap control, while
    // form4 returned 0 to 64. Reporting only the legacy one would record that
    // disagreement as a coverage finding. Which endpoint is wrong, and why, is
    // not something this probe measured [K.2].
    var cutoff = today.AddDays(-90);

    using (var doc = await Get("insider-transactions", true,
        ("code", ticker),
        ("from", cutoff.ToString("yyyy-MM-dd", inv)),
        ("to", todayStr),
        ("limit", "1000")))
    {
        if (doc is null) Say("    legacy insider-transactions: no result");
        else
        {
            var owners = new HashSet<string>(StringComparer.Ordinal);
            var n = doc.RootElement.GetArrayLength();
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var o = Str(t, "ownerName");
                if (o is not null) owners.Add(o);
            }
            Say($"    legacy endpoint 90d        {I(n)} transactions, {I(owners.Count)} distinct insiders");
        }
    }

    // form4, paged newest-first until past the 90-day cutoff.
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var byCode = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var txns = 0;
        var filings = 0;
        double netPurchaseUsd = 0;
        var buyers = new HashSet<string>(StringComparer.Ordinal);
        string? newestFiled = null;
        var total = 0;
        var offset = 0;
        var done = false;

        while (!done && offset < 400)
        {
            using var doc = await Get($"sec-filings/{ticker}/form4", true,
                ("page[limit]", "100"), ("page[offset]", offset.ToString(inv)));
            if (doc is null) break;
            if (doc.RootElement.TryGetProperty("meta", out var meta)
                && meta.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number)
            {
                total = tot.GetInt32();
            }
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
            if (data.GetArrayLength() == 0) break;

            foreach (var filing in data.EnumerateArray())
            {
                var filed = Str(filing, "filed_at");
                if (filed is not null && (newestFiled is null || string.CompareOrdinal(filed, newestFiled) > 0))
                {
                    newestFiled = filed;
                }
                filings++;

                foreach (var section in new[] { "non_derivative", "derivative" })
                {
                    if (!filing.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                    foreach (var t in arr.EnumerateArray())
                    {
                        var td = Str(t, "transaction_date");
                        if (td is null || td.Length < 10) continue;
                        if (!DateOnly.TryParse(td[..10], inv, out var d)) continue;
                        if (d < cutoff) { done = true; continue; }

                        txns++;
                        var owner = Str(t, "reporting_owner_name");
                        if (owner is not null) owners.Add(owner);
                        var code = Str(t, "transaction_code") ?? "(null)";
                        byCode[code] = byCode.TryGetValue(code, out var v) ? v + 1 : 1;

                        // P is an open-market purchase. The S4 rubric weighs those
                        // and disqualifies option exercises and plan activity.
                        if (code == "P")
                        {
                            var val = Num(t, "total_value")
                                      ?? (Num(t, "shares_amount") ?? 0) * (Num(t, "price_per_share") ?? 0);
                            netPurchaseUsd += val;
                            if (owner is not null) buyers.Add(owner);
                        }
                    }
                }
            }
            offset += 100;
            if (total > 0 && offset >= total) break;
        }

        Say($"    form4 filings scanned      {I(filings)} of {I(total)} total, newest filed {newestFiled ?? "none"}");
        Say($"    form4 transactions 90d     {I(txns)}");
        Say($"    distinct insiders 90d      {I(owners.Count)}");
        Say($"    by transaction_code        "
            + (byCode.Count == 0 ? "(none)" : string.Join("  ", byCode.Select(k => $"{k.Key}={I(k.Value)}"))));
        Say($"    open-market buyers (P)     {I(buyers.Count)}, net purchase USD {ShowInt(netPurchaseUsd)}");
        Say("      the code breakdown matters: the S4 rubric disqualifies option");
        Say("      exercises and scheduled plan activity, so a bare transaction count");
        Say("      overstates what distinct_buyer_count would actually see.");
    }

    // --- short interest ---------------------------------------------------
    using (var doc = await Get($"fundamentals/{ticker}", true, ("filter", "Technicals,SharesStats")))
    {
        if (doc is null) Say("    short interest: fundamentals returned no result");
        else
        {
            var r = doc.RootElement;
            var tech = r.TryGetProperty("Technicals", out var te) ? te : default;
            var ss = r.TryGetProperty("SharesStats", out var se) ? se : default;

            Say($"    Technicals.SharesShort            {ShowInt(Num(tech, "SharesShort"))}");
            Say($"    Technicals.SharesShortPriorMonth  {ShowInt(Num(tech, "SharesShortPriorMonth"))}");
            Say($"    Technicals.ShortPercent           {Show(Num(tech, "ShortPercent"))}");
            Say($"    Technicals.ShortRatio             {Show(Num(tech, "ShortRatio"))}");
            Say($"    SharesStats.SharesShort           {ShowInt(Num(ss, "SharesShort"))}");
            Say($"    SharesStats.ShortPercentFloat     {Show(Num(ss, "ShortPercentFloat"))}");

            var now = Num(tech, "SharesShort");
            var prior = Num(tech, "SharesShortPriorMonth");
            Say($"    one-month change computable       {(now is not null && prior is not null ? "yes" : "no")}");

            var hasDate = false;
            if (tech.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in tech.EnumerateObject())
                {
                    if (p.Name.Contains("Date", StringComparison.OrdinalIgnoreCase)) hasDate = true;
                }
            }
            Say($"    any as-of date on short interest  {(hasDate ? "yes" : "no")}");
        }
    }

    // Does a historical form exist at all? Tested rather than inferred.
    using (var doc = await Get($"fundamentals/{ticker}", true,
        ("filter", "Technicals"), ("historical", "1"),
        ("from", today.AddDays(-180).ToString("yyyy-MM-dd", inv)), ("to", todayStr)))
    {
        if (doc is null) Say("    short interest history: request failed");
        else
        {
            var kind = doc.RootElement.ValueKind;
            var n = kind == JsonValueKind.Array ? doc.RootElement.GetArrayLength()
                  : kind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().Count()
                  : 0;
            var isSeries = kind == JsonValueKind.Array
                || (kind == JsonValueKind.Object && doc.RootElement.EnumerateObject()
                        .Any(p => p.Name.Length == 10 && p.Name[4] == '-'));
            Say($"    historical=1 returns              {kind}, {I(n)} members, "
                + $"date-keyed series: {(isSeries ? "yes" : "no")}");
            Say($"    short interest observations 180d  {(isSeries ? I(n) : "0 (snapshot only, no series)")}");
        }
    }

    // --- institutional ownership -----------------------------------------
    using (var doc = await Get($"fundamentals/{ticker}", true,
        ("filter", "SharesStats::PercentInstitutions,SharesStats::PercentInsiders")))
    {
        if (doc is null) Say("    ownership percentages: no result");
        else
        {
            var r = doc.RootElement;
            Say($"    SharesStats.PercentInstitutions   {Show(Num(r, "SharesStats::PercentInstitutions"))}");
            Say($"    SharesStats.PercentInsiders       {Show(Num(r, "SharesStats::PercentInsiders"))}");
        }
    }

    using (var doc = await Get($"fundamentals/{ticker}", true, ("filter", "Holders::Institutions")))
    {
        if (doc is null) Say("    Holders::Institutions: no result");
        else
        {
            var entries = 0;
            string? newest = null;
            var hasChange = false;
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    entries++;
                    var d = Str(p.Value, "date");
                    if (d is not null && (newest is null || string.CompareOrdinal(d, newest) > 0)) newest = d;
                    if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("change", out _)) hasChange = true;
                }
            }
            Say($"    Holders::Institutions entries     {I(entries)}");
            Say($"    newest date on any entry          {newest ?? "null"}");
            Say($"    entries carry a change field      {(hasChange ? "yes" : "no")}");
            Say("      the date is the point: inst_ownership_change needs a change, so");
            Say("      whether anything dates the payload decides whether it is backfillable.");
        }
    }
}

// =================================================== P.4.4 FILING DATES ===

async Task FilingDates(IReadOnlyList<string> tickerList)
{
    Rule("P.4.4  FILING DATES");
    Say("  D-46 and ARCH section 14 both assume a period-end to filing gap of about");
    Say("  five weeks. Absent, null and equal-to-period-end are printed as distinct");
    Say("  states rather than folded together.");
    Say("  Every quarter the provider returns, not the newest eight [H.4]. The eight");
    Say("  quarter bound is what put the RJET claim beyond this tool's reach and what");
    Say("  left the 65 day constant resting on 56 quarters that D-62 superseded.");
    Say("  D-62 substitutes each ticker's own widest clean gap observed to date and");
    Say("  excludes a ticker with fewer than four clean gaps, so both are reported per");
    Say("  ticker below and no universal threshold is tested [K.2].");

    foreach (var ticker in tickerList)
    {
        Say("");
        Say($"  {ticker}");

        async Task<List<(string Period, string? Date, string? Filing)>> Quarters(string statement)
        {
            var outp = new List<(string, string?, string?)>();
            using var doc = await Get($"fundamentals/{ticker}", true,
                ("filter", $"Financials::{statement}::quarterly"));
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return outp;
            foreach (var p in doc.RootElement.EnumerateObject()
                         .OrderByDescending(p => p.Name, StringComparer.Ordinal))
            {
                outp.Add((p.Name, Str(p.Value, "date"), Str(p.Value, "filing_date")));
            }
            return outp;
        }

        var bs = await Quarters("Balance_Sheet");
        var isx = await Quarters("Income_Statement");

        var gaps = new List<int>();
        int equal = 0, nulls = 0, unparseable = 0, equalIn12 = 0;

        if (bs.Count == 0) Say("    Balance_Sheet quarterly: nothing returned");
        else
        {
            Say($"    {"period_end",-12} {"filing_date",-12} {"gap days",-9} state");
            var idx = 0;
            foreach (var (period, date, filing) in bs)
            {
                var pe = date ?? period;
                string gap, state;
                if (filing is null) { gap = "-"; state = "FILING DATE ABSENT OR NULL"; nulls++; }
                else if (string.Equals(filing, pe, StringComparison.Ordinal))
                {
                    gap = "0"; state = "EQUAL TO PERIOD END"; equal++;
                    if (idx < 12) equalIn12++;
                }
                else if (DateOnly.TryParse(pe, inv, out var p1) && DateOnly.TryParse(filing, inv, out var f1))
                {
                    var g = f1.DayNumber - p1.DayNumber;
                    gaps.Add(g);
                    gap = g.ToString(inv);
                    // D-62 replaced D-57's universal constant with each ticker's own
                    // widest clean gap, so there is no fixed threshold for a row to
                    // exceed and none is tested. A gap under one day is unknown under
                    // D-62 rather than early. The gap list is left exactly as measured
                    // so the recorded H.4 aggregates still reproduce: classifying is
                    // the ingestor's job at 1.4, not this instrument's [K.2].
                    state = g < 1 ? "ok, UNDER ONE DAY, UNKNOWN UNDER D-62" : "ok";
                }
                else { gap = "?"; state = "UNPARSEABLE"; unparseable++; }
                Say($"    {pe,-12} {filing ?? "null",-12} {gap,-9} {state}");
                idx++;
            }

            Say("");
            Say($"    total periods                    {I(bs.Count)}");
            Say($"    filing_date equal to period_end  {I(equal)}");
            Say($"    filing_date null or absent       {I(nulls)}");
            Say($"    unparseable                      {I(unparseable)}");
            Say($"    equal within the newest 12       {I(equalIn12)} of {I(Math.Min(12, bs.Count))}");

            if (gaps.Count > 0)
            {
                gaps.Sort();
                Say($"    clean gaps                       {I(gaps.Count)}, min {I(gaps[0])}, "
                    + $"max {I(gaps[^1])}, median {I(gaps[gaps.Count / 2])}");
                Say($"    D-62 substitution, widest gap    {I(gaps[^1])}"
                    + (gaps.Count < MinCleanGapsForSubstitution
                        ? $"   TICKER EXCLUDED, under {I(MinCleanGapsForSubstitution)} clean gaps"
                        : ""));
                Say("      full history, not point in time. D-62 counts only gaps");
                Say("      observable before the read date, which the ingestor applies");
                Say("      at 1.4 and this instrument does not.");
                var hist = new SortedDictionary<int, int>();
                foreach (var g in gaps) hist[g] = hist.TryGetValue(g, out var c) ? c + 1 : 1;
                Say("    full gap distribution, days=count:");
                Say("      " + string.Join("  ", hist.Select(kv => $"{I(kv.Key)}={I(kv.Value)}")));
            }
            else Say("    clean gaps                       none");
        }

        // Cross-check: the income statement must agree, or one of them is wrong.
        var disagree = 0;
        foreach (var (period, _, filing) in bs)
        {
            var other = isx.FirstOrDefault(x => x.Period == period);
            if (other.Period is null) continue;
            if (!string.Equals(other.Filing, filing, StringComparison.Ordinal)) disagree++;
        }
        Say($"    income statement cross-check: {I(isx.Count)} periods, "
            + (disagree == 0 ? "filing dates agree" : $"{I(disagree)} DISAGREE with balance sheet"));
    }
}

await FilingDates(tickers);


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

Console.WriteLine($"\ntranscript written to {SaveTranscript("probe")}");

return 0;

// Transcripts are repository evidence rather than build output, so they are
// written beside the project instead of under bin/, where .gitignore excludes
// them and the numbers in PROGRESS.md end up with nothing behind them [H.1].
static string ProjectDir()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null)
    {
        if (File.Exists(Path.Combine(d.FullName, "probe.csproj"))) return d.FullName;
        d = d.Parent;
    }
    return AppContext.BaseDirectory;
}

string SaveTranscript(string prefix)
{
    var outDir = Path.Combine(ProjectDir(), "probe-output");
    Directory.CreateDirectory(outDir);
    var outPath = Path.Combine(outDir, $"{prefix}-{startedUtc.ToString("yyyyMMdd-HHmmss", inv)}.txt");
    File.WriteAllText(outPath, Redact(transcript.ToString()));
    return outPath;
}
