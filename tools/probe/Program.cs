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

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

// P.2 scaffold target. P.3 replaces this with programmatic selection.
const string SmokeTicker = "AAPL.US";

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

// ------------------------------------------------------------------ http ---

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var httpRequests = 0;

// Returns null on any failure. Callers check and move to the next measurement.
async Task<JsonDocument?> Get(string path, params (string Key, string Value)[] query)
{
    var parts = new List<string>();
    foreach (var (k, v) in query)
    {
        parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
    }
    parts.Add("fmt=json");
    parts.Add($"api_token={Uri.EscapeDataString(token)}");

    var url = $"https://eodhd.com/api/{path}?{string.Join("&", parts)}";
    Say($"  GET {Redact(url)}");

    try
    {
        httpRequests++;
        using var resp = await http.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
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

string Show(double? d) => d is null ? "null" : d.Value.ToString("0.####", inv);
string ShowInt(double? d) => d is null ? "null" : ((long)d.Value).ToString(inv);

// ================================================================ RUN =====

var startedUtc = DateTime.UtcNow;
Say("Phase P data probe");
Say($"Scaffold run (P.2), single ticker: {SmokeTicker}");

// --------------------------------------------------------------- account ---

Rule("ACCOUNT");

using (var doc = await Get("user"))
{
    if (doc is null)
    {
        Say("  account endpoint unavailable, continuing");
    }
    else
    {
        var r = doc.RootElement;
        Say($"  subscriptionType  {Str(r, "subscriptionType") ?? "null"}");
        Say($"  subscriptionMode  {Str(r, "subscriptionMode") ?? "null"}");
        Say($"  apiRequests       {ShowInt(Num(r, "apiRequests"))}");
        Say($"  apiRequestsDate   {Str(r, "apiRequestsDate") ?? "null"}");
        Say($"  dailyRateLimit    {ShowInt(Num(r, "dailyRateLimit"))}");
        Say($"  extraLimit        {ShowInt(Num(r, "extraLimit"))}");
    }
}

// ------------------------------------------------ endpoint reachability ---

Rule($"MEASUREMENT REACHABILITY, {SmokeTicker}");

var today = DateOnly.FromDateTime(startedUtc);
var from90 = today.AddDays(-90).ToString("yyyy-MM-dd", inv);
var todayStr = today.ToString("yyyy-MM-dd", inv);

Say("");
Say("news");
using (var doc = await Get("news", ("s", SmokeTicker), ("from", from90), ("to", todayStr), ("limit", "5")))
{
    if (doc is null) Say("  no result");
    else
    {
        var n = doc.RootElement.GetArrayLength();
        Say($"  array length {n.ToString(inv)}"
            + (n > 0 ? $", first date {Str(doc.RootElement[0], "date") ?? "null"}" : ""));
    }
}

Say("");
Say("sentiments");
using (var doc = await Get("sentiments", ("s", SmokeTicker), ("from", from90), ("to", todayStr)))
{
    if (doc is null) Say("  no result");
    else
    {
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Say($"  keys [{string.Join(", ", keys)}]");
        if (keys.Count > 0)
        {
            Say($"  rows {doc.RootElement.GetProperty(keys[0]).GetArrayLength().ToString(inv)}");
        }
    }
}

Say("");
Say("insider-transactions (legacy)");
using (var doc = await Get("insider-transactions", ("code", SmokeTicker), ("from", from90), ("to", todayStr), ("limit", "10")))
{
    Say(doc is null ? "  no result" : $"  array length {doc.RootElement.GetArrayLength().ToString(inv)}");
}

Say("");
Say("sec-filings form4 (current)");
using (var doc = await Get($"sec-filings/{SmokeTicker}/form4"))
{
    Say(doc is null ? "  no result" : $"  root kind {doc.RootElement.ValueKind}");
}

Say("");
Say("fundamentals, short interest fields");
using (var doc = await Get($"fundamentals/{SmokeTicker}", ("filter", "Technicals")))
{
    if (doc is null) Say("  no result");
    else
    {
        var r = doc.RootElement;
        Say($"  Technicals.SharesShort            {ShowInt(Num(r, "SharesShort"))}");
        Say($"  Technicals.SharesShortPriorMonth  {ShowInt(Num(r, "SharesShortPriorMonth"))}");
        Say($"  Technicals.ShortPercent           {Show(Num(r, "ShortPercent"))}");
    }
}

Say("");
Say("fundamentals, quarterly balance sheet");
using (var doc = await Get($"fundamentals/{SmokeTicker}", ("filter", "Financials::Balance_Sheet::quarterly")))
{
    if (doc is null) Say("  no result");
    else
    {
        var periods = doc.RootElement.EnumerateObject()
            .Select(p => p.Name).OrderDescending(StringComparer.Ordinal).ToList();
        Say($"  periods {periods.Count.ToString(inv)}");
        if (periods.Count > 0)
        {
            var q = doc.RootElement.GetProperty(periods[0]);
            Say($"  newest date {Str(q, "date") ?? "null"}, filing_date {Str(q, "filing_date") ?? "null"}");
        }
    }
}

Say("");
Say("screener (availability only, not used for selection)");
using (var doc = await Get("screener",
    ("filters", "[[\"market_capitalization\",\">=\",300000000],[\"market_capitalization\",\"<\",2000000000],[\"exchange\",\"=\",\"us\"]]"),
    ("limit", "1")))
{
    Say(doc is null ? "  not available on this plan" : "  available");
}

Say("");
Say("eod-bulk-last-day/US");
using (var doc = await Get("eod-bulk-last-day/US", ("filter", "extended")))
{
    if (doc is null) Say("  no result");
    else
    {
        var n = doc.RootElement.GetArrayLength();
        Say($"  rows {n.ToString(inv)}");
        if (n > 0)
        {
            Say($"  first code {Str(doc.RootElement[0], "code") ?? "null"}"
                + $", date {Str(doc.RootElement[0], "date") ?? "null"}");
        }
    }
}

// ----------------------------------------------------------------- close ---

Rule("RUN SUMMARY");
Say($"  http requests issued  {httpRequests.ToString(inv)}");

using (var doc = await Get("user"))
{
    if (doc is not null)
    {
        Say($"  apiRequests after     {ShowInt(Num(doc.RootElement, "apiRequests"))}");
    }
}

var outDir = Path.Combine(AppContext.BaseDirectory, "probe-output");
Directory.CreateDirectory(outDir);
var outPath = Path.Combine(outDir, $"probe-{startedUtc.ToString("yyyyMMdd-HHmmss", inv)}.txt");
await File.WriteAllTextAsync(outPath, Redact(transcript.ToString()));
Console.WriteLine($"\ntranscript written to {outPath}");

return 0;
