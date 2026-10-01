using System.Text.RegularExpressions;
using Npgsql;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Stages;

/// <summary>
/// Checkpoint 5.5.8. INVARIANT 16's C# half: money is decimal in any monetary path, checked
/// at the value that carries it [operator direction 2026-10-01].
///
/// **The schema half has been asserted since D-83 and the C# half by nothing.** `guards.ps1`
/// replaced its `float|double` source grep with a schema assertion, which holds every
/// monetary column `numeric` and says nothing about the value a component computes and
/// hands it. The row first authored here asked for that grep scoped from the registry, and
/// it could not be green: `IndicatorEngine` owns `median_dollar_volume_20d` and its file holds
/// 108 uses of `double`, every one in arithmetic that writes `real` columns, the monetary
/// value itself arriving from SQL as `decimal?` and leaving as `decimal`. **The operator
/// chose the check at the carrier instead**: the C# value bound for a `numeric` column must
/// be `decimal`, and a statement computing one must not pass it through floating point. A
/// `double` elsewhere in a file is not this check's to catch.
///
/// **`numeric` is the money type in this schema**, so the scope is every `numeric` column a
/// component writes, read off the live schema rather than off a name pattern. That includes
/// money the monetary name pattern does not reach, such as `fundamental_snapshot`'s figures.
/// </summary>
[Collection("database")]
public sealed class MonetaryCarrierConformanceTests
{
    private static readonly DateOnly Date = new(1995, 5, 1);
    private const string Ticker = "SRL558.US";

    private static readonly string[] Columns =
        ["ticker", "date", "sector", "size_bucket", "market_cap", "is_active"];

    // ------------------------------------------------------- the bulk route ---

    /// <summary>
    /// **A `double` bound for a `numeric` column is refused before the row is written**, by
    /// the bulk writer, naming the table, the column and the invariant. A `decimal` in the
    /// same place lands.
    /// </summary>
    [Fact]
    public async Task ADoubleBoundForANumericColumnIsRefusedBeforeTheRowIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => WriteAsync(w => w.WriteAsync(1_000_000_000d, ct), ct));

            Assert.Contains("security_daily.market_cap", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("INVARIANT 16", thrown.Message, StringComparison.Ordinal);
            Assert.Equal(0L, await RowsAsync(ct));

            await WriteAsync(w => w.WriteAsync(1_000_000_000m, ct), ct);
            Assert.Equal(1L, await RowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// A null carried as `double?` is refused too. It holds nothing tonight and it is still a
    /// monetary path in floating point; the night it holds a value is the night it rounds.
    /// </summary>
    [Fact]
    public async Task ANullCarriedAsADoubleIsRefusedToo()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WriteAsync(w => w.WriteAsync<double?>(null, ct), ct));

            Assert.Equal(0L, await RowsAsync(ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    // --------------------------------------------------- the statement route ---

    /// <summary>
    /// **No statement carries a `numeric` column through floating point.** A value bound
    /// through a parameter is held to the C# declaration of what is bound, which must be
    /// `decimal`. A value computed in SQL is held to its path: the select-list item that
    /// writes it and every CTE that item draws from, transitively, must carry no cast to a
    /// floating type. A cast elsewhere in the statement, on a column that is not money, is
    /// not this check's.
    /// </summary>
    [Fact]
    public async Task NoStatementCarriesMoneyThroughFloatingPoint()
    {
        var numeric = await NumericColumnsAsync(TestContext.Current.CancellationToken);
        var files = SourceTree.Files();

        var violations = StatementCatalogue.All()
            .SelectMany(s => Violations(s.Stage, s.Table, s.Sql, numeric, SourceTree.Of(files, s.Stage)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        Assert.True(violations.Count == 0,
            "Statements carry money through floating point or bind it from a value not declared decimal:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// The statement check discriminates in both of its forms. `FlowEngine`'s net dollar
    /// figure cast to `float8` inside the CTE that sums it is reported, while the `::real` the
    /// statement already carries, on the ownership change, is not. And `CostLedger` with its
    /// cost declared `double` is reported.
    /// </summary>
    [Fact]
    public async Task TheStatementCheckFailsOnMoneyCarriedThroughFloatingPoint()
    {
        var numeric = await NumericColumnsAsync(TestContext.Current.CancellationToken);
        var files = SourceTree.Files();

        Assert.Contains("::real", FlowEngine.Sql, StringComparison.Ordinal);
        Assert.Empty(Violations("FlowEngine", "flow_daily", FlowEngine.Sql, numeric, SourceTree.Of(files, "FlowEngine")));

        const string sum = "END AS net_usd";
        Assert.Contains(sum, FlowEngine.Sql, StringComparison.Ordinal);

        var cast = FlowEngine.Sql.Replace(sum, "END::float8 AS net_usd", StringComparison.Ordinal);
        Assert.Contains(
            Violations("FlowEngine", "flow_daily", cast, numeric, SourceTree.Of(files, "FlowEngine")),
            v => v.Contains("flow_daily.insider_net_90d_usd", StringComparison.Ordinal));

        var ledger = SourceTree.Of(files, "CostLedger");
        Assert.Contains("decimal cost =", ledger, StringComparison.Ordinal);

        var doubled = ledger.Replace("decimal cost =", "double cost =", StringComparison.Ordinal);
        Assert.Contains(
            Violations("CostLedger", "cost_ledger", CostLedger.InsertSql, numeric, doubled),
            v => v.Contains("cost_ledger.cost", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------ the check ---

    private static readonly Regex FloatCast = new(
        @"::\s*(?:real|float4|float8|double\s+precision)\b|\bCAST\s*\([^)]*\bAS\s+(?:real|float\w*|double)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<string> Violations(
        string stage, string table, string sql, IReadOnlyDictionary<string, IReadOnlySet<string>> numeric, string source)
    {
        var violations = new List<string>();

        if (!numeric.TryGetValue(table, out var money))
        {
            return violations;
        }

        var text = Regex.Replace(sql, @"--[^\r\n]*", string.Empty);
        var insert = Regex.Match(text, $@"INSERT\s+INTO\s+""?{Regex.Escape(table)}""?\s*\((?<c>[^)]*)\)");

        if (!insert.Success)
        {
            return violations;
        }

        var columns = insert.Groups["c"].Value.Split(',').Select(c => c.Trim()).ToList();
        var rest = text[(insert.Index + insert.Length)..];

        for (var k = 0; k < columns.Count; k++)
        {
            if (!money.Contains(columns[k]))
            {
                continue;
            }

            var values = Regex.Match(rest, @"^\s*VALUES\s*\((?<v>[^)]*)\)");

            if (values.Success)
            {
                // Bound through a parameter: the C# value bound must be declared decimal.
                var token = values.Groups["v"].Value.Split(',').Select(v => v.Trim()).ElementAtOrDefault(k);

                if (token is not null && token.StartsWith('@'))
                {
                    var bound = Regex.Match(source, $@"AddWithValue\(\s*""{Regex.Escape(token[1..])}""\s*,\s*(?<e>[A-Za-z_]\w*)\s*\)");
                    var declared = bound.Success
                        ? Regex.Match(source, $@"(?<type>\b[A-Za-z_]\w*\??)\s+{Regex.Escape(bound.Groups["e"].Value)}\s*=")
                        : Match.Empty;

                    if (!declared.Success || declared.Groups["type"].Value is not ("decimal" or "decimal?"))
                    {
                        violations.Add($"{stage} -> {table}.{columns[k]}: bound from {token}, whose C# value is " +
                                       (declared.Success ? $"declared {declared.Groups["type"].Value}" : "not found declared"));
                    }
                }

                continue;
            }

            // Computed in SQL: the select-list item that writes it, and the CTEs it draws from.
            foreach (var path in SqlPath(rest, k).Where(p => FloatCast.IsMatch(p.Text)))
            {
                violations.Add($"{stage} -> {table}.{columns[k]}: a floating cast in {path.Where}");
            }
        }

        return violations;
    }

    /// <summary>
    /// The parts of an <c>INSERT ... WITH ... SELECT</c> that compute output column
    /// <paramref name="k"/>: the final select item and, transitively, every CTE it draws from
    /// by the aliases it names.
    /// </summary>
    private static IEnumerable<(string Where, string Text)> SqlPath(string rest, int k)
    {
        var ctes = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;

        foreach (Match m in Regex.Matches(rest, @"(?<name>[a-z_][a-z0-9_]*)\s+AS\s+\("))
        {
            if (m.Index < at)
            {
                continue;
            }

            var open = m.Index + m.Length;
            var depth = 1;
            var i = open;

            for (; i < rest.Length && depth > 0; i++)
            {
                depth += rest[i] == '(' ? 1 : rest[i] == ')' ? -1 : 0;
            }

            ctes[m.Groups["name"].Value] = rest[open..(i - 1)];
            at = i;
        }

        var final = rest[at..];
        var select = Regex.Match(final, @"SELECT\s+(?<list>.+?)\s+FROM\s", RegexOptions.Singleline);
        var item = select.Success ? SplitTopLevel(select.Groups["list"].Value).ElementAtOrDefault(k) ?? string.Empty : string.Empty;

        yield return ("the select-list item that writes it", item);

        var aliases = Regex.Matches(final, @"(?:FROM|JOIN)\s+(?<cte>[a-z_][a-z0-9_]*)\s+(?<alias>[a-z_][a-z0-9_]*)\b")
            .Where(m => ctes.ContainsKey(m.Groups["cte"].Value))
            .ToDictionary(m => m.Groups["alias"].Value, m => m.Groups["cte"].Value, StringComparer.Ordinal);

        var tainted = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(Regex.Matches(item, @"\b(?<a>[a-z_][a-z0-9_]*)\.")
            .Select(m => m.Groups["a"].Value)
            .Where(aliases.ContainsKey)
            .Select(a => aliases[a]));

        while (queue.Count > 0)
        {
            var cte = queue.Dequeue();

            if (!tainted.Add(cte))
            {
                continue;
            }

            foreach (Match m in Regex.Matches(ctes[cte], @"(?:FROM|JOIN)\s+(?<cte>[a-z_][a-z0-9_]*)\b"))
            {
                if (ctes.ContainsKey(m.Groups["cte"].Value))
                {
                    queue.Enqueue(m.Groups["cte"].Value);
                }
            }
        }

        foreach (var cte in tainted)
        {
            yield return ($"the CTE {cte}", ctes[cte]);
        }
    }

    private static List<string> SplitTopLevel(string list)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < list.Length; i++)
        {
            depth += list[i] == '(' ? 1 : list[i] == ')' ? -1 : 0;

            if (list[i] == ',' && depth == 0)
            {
                parts.Add(list[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(list[start..].Trim());
        return parts;
    }

    // ------------------------------------------------------------- plumbing ---

    private static async Task WriteAsync(Func<IBulkWriter, Task> marketCap, CancellationToken ct)
    {
        var data = new StageData(
            TestDatabase.ConnectionString,
            new DeclaredAccess("TestMonetaryCarrier", [], [new TableWrite("security_daily", WriteOperation.Insert, Columns)]));

        await data.BulkUpsertAsync("security_daily", Columns, ["ticker", "date"], async (w, c) =>
        {
            await w.StartRowAsync(c).ConfigureAwait(false);
            await w.WriteAsync(Ticker, c).ConfigureAwait(false);
            await w.WriteAsync(Date, c).ConfigureAwait(false);
            await w.WriteAsync("srltest-money", c).ConfigureAwait(false);
            await w.WriteAsync("small", c).ConfigureAwait(false);
            await marketCap(w).ConfigureAwait(false);
            await w.WriteAsync(true, c).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> NumericColumnsAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name, column_name FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND data_type = 'numeric';", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);

        var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        while (await r.ReadAsync(ct).ConfigureAwait(true))
        {
            if (!found.TryGetValue(r.GetString(0), out var set))
            {
                found[r.GetString(0)] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(r.GetString(1));
        }

        return found.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>) kv.Value, StringComparer.Ordinal);
    }

    private static async Task<long> RowsAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM security_daily WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", Ticker);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand("DELETE FROM security_daily WHERE ticker = @t;", conn);
        cmd.Parameters.AddWithValue("t", Ticker);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
    }
}
