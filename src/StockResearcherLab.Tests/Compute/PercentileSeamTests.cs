using System.Globalization;
using Npgsql;
using StockResearcherLab.Pipeline.Compute;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Compute;

/// <summary>
/// C11's seam, and the fallback firing over a range [3.15].
///
/// **C11 is the one component where the range path and the nightly path issue the
/// identical statement**, so what the seam can lose is not the arithmetic but the bounds
/// around it: which date each UPDATE names, which config version its floor came from, and
/// whether the fallback counts a range reports are the range's or one date's.
///
/// **The range spans five dates and each carries a different population**, which is the
/// lesson 3.13 and 3.14 both paid for. A fixture identical on every date passes against a
/// range path that ranks one date and writes the answer to all of them, and that mutation
/// was run against this file rather than reasoned about.
/// </summary>
[Collection("database")]
public sealed class PercentileSeamTests
{
    private const string Prefix = "SRLPCT";

    /// <summary>`percentile.cell_min_members`, which is the floor a sector cell has to clear.</summary>
    private const int Floor = 15;

    /// <summary>
    /// Sixteen names in one size bucket. **Above the floor in the bucket and below it in
    /// either sector**, which is what makes the fallback fire: the thin sector cell cannot
    /// rank, the bucket can, and the run log has to say so per metric.
    /// </summary>
    private const int Members = 16;

    private const string Thin = "SRLTEST-PCT-THIN";

    private const string Rest = "SRLTEST-PCT-REST";

    private const string Bucket = "SRLTEST-PCT-BUCKET";

    private const string Query =
        "SELECT ticker, dist_200dma_pctile, atr_pct_pctile " +
        "FROM indicator_daily WHERE date = @d AND ticker LIKE 'SRLPCT%' ORDER BY ticker;";

    /// <summary>
    /// **The anchor.** Every date of a range ranked both ways and compared.
    ///
    /// The percentile columns are an UPDATE over rows that already exist, so they are
    /// nulled between the two runs. Without that the range would upsert over the nightly
    /// answer wherever the two agreed and the comparison would be of a snapshot with
    /// itself, which is the same trap C09's seam had to be rescued from.
    /// </summary>
    [Fact]
    public async Task TheRangePathRanksEveryDateTheSameWayTheNightlyPathDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        var stage = new PercentileEngine();
        var nightly = new Dictionary<DateOnly, IReadOnlyList<IReadOnlyList<object?>>>();

        foreach (var date in Seam.Dates())
        {
            await ClearPercentilesAsync(ct).ConfigureAwait(true);
            await Seam.RunNightlyAsync(stage, date, ct).ConfigureAwait(true);
            nightly[date] = await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true);
        }

        await ClearPercentilesAsync(ct).ConfigureAwait(true);
        await Seam.RunRangeAsync(stage, ct).ConfigureAwait(true);

        foreach (var date in Seam.Dates())
        {
            var range = await Seam.RowsAsync(Query, date, ct).ConfigureAwait(true);

            Assert.Equal(Members, nightly[date].Count);

            // Ranked rather than all null, which a range path that never ran would also
            // produce and which an equality alone cannot tell apart.
            Assert.Contains(nightly[date], r => r[1] is not null);

            Assert.Equal(nightly[date].Count, range.Count);

            for (var i = 0; i < range.Count; i++)
            {
                Assert.Equal(nightly[date][i], range[i]);
            }
        }

        // **Every date ranks differently**, which is what makes the equality above a
        // statement about per-date work. Without it a range path ranking one date and
        // writing that answer to all five passes.
        var shapes = Seam.Dates()
            .Select(d => string.Join("|", nightly[d].Select(r => Convert.ToString(r[1], CultureInfo.InvariantCulture))))
            .ToList();

        Assert.Equal(shapes.Count, shapes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// **The fallback fires and the range reports it per metric**, which is 3.15's second
    /// done-when clause.
    ///
    /// Sixteen names split across two sectors of eight leaves every sector cell under the
    /// fifteen-member floor while the bucket clears it, so every metric ranks in the
    /// bucket and none in a sector cell. The range's own detail line has to carry that,
    /// summed over the range rather than reported for one date, because a per-date line
    /// over twelve hundred dates is not a line anybody reads.
    /// </summary>
    [Fact]
    public async Task TheThinSectorFallbackFiresAndTheRangeLineCountsItPerMetric()
    {
        var ct = TestContext.Current.CancellationToken;
        await ResetAsync(ct).ConfigureAwait(true);

        await ClearPercentilesAsync(ct).ConfigureAwait(true);

        var detail = await Seam.RunRangeReportingAsync(new PercentileEngine(), ct).ConfigureAwait(true);

        Assert.NotNull(detail);

        // The floor and the range are both stated, so an operator reads what was applied
        // rather than inferring it.
        Assert.Contains("floor 15", detail, StringComparison.Ordinal);
        Assert.Contains($"{Seam.Dates().Count:N0} trading date(s)", detail, StringComparison.Ordinal);

        // **The per-date wall clock reaches the line through the real range path** [3.17].
        // A compute stage is not run twice, so instrumentation that compiles and does not
        // emit is a figure the phase cannot recover: the run it was owed from is gone.
        // One unit per date of the fixture's range, which is what says the stopwatch is
        // inside the loop rather than around it.
        Assert.Contains("Per date: first", detail, StringComparison.Ordinal);
        Assert.Contains($"over {Seam.Dates().Count:N0} unit(s)", detail, StringComparison.Ordinal);

        // **And the spans outside the loop, for the same reason** [item 43]. C08's run
        // reported 22 chunks against a stage duration more than twice their sum, and the
        // missing half was never timed because nothing above the loop was instrumented.
        // A per-unit line that accounts for part of a stage while reading as though it
        // accounts for the whole is the failure that item records.
        Assert.Contains("Outside the work loop: calendar", detail, StringComparison.Ordinal);
        Assert.Contains("ms in all", detail, StringComparison.Ordinal);

        // The counts are the range's, not one date's: every one of the sixteen names ranks
        // in the bucket on each of the five dates, for the metric the fixture fills.
        Assert.Contains("in the size bucket alone", detail, StringComparison.Ordinal);

        // And the per-metric breakdown names a metric of the table this fixture fills.
        Assert.Contains("indicator_daily.dist_200dma", detail, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------- harness ---

    private static string Ticker(int i)
        => Prefix + i.ToString("00", CultureInfo.InvariantCulture) + ".US";

    /// <summary>
    /// Both percentile columns this fixture reads, back to null.
    ///
    /// By ticker prefix rather than by date, because the UPDATE rewrites every row for a
    /// date and a stale value left on one of them is exactly what the comparison would
    /// otherwise read as agreement.
    /// </summary>
    private static Task ClearPercentilesAsync(CancellationToken ct)
        => Seam.ExecuteAsync(
            "UPDATE indicator_daily SET dist_200dma_pctile = NULL, atr_pct_pctile = NULL " +
            "WHERE ticker LIKE @p;",
            c => c.Parameters.AddWithValue("p", Prefix + "%"), ct);

    /// <summary>
    /// Sixteen members of one size bucket, split eight and eight across two sectors so
    /// neither cell reaches the floor. `dist_200dma` moves with both the ticker and the
    /// date, so no two dates rank alike.
    /// </summary>
    private static async Task ResetAsync(CancellationToken ct)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM indicator_daily WHERE ticker LIKE @p",
                     "DELETE FROM security_daily WHERE ticker LIKE @p",
                     "DELETE FROM price_daily WHERE ticker LIKE @p",
                 })
        {
            await Seam.ExecuteAsync(sql, c => c.Parameters.AddWithValue("p", Prefix + "%"), ct)
                .ConfigureAwait(false);
        }

        for (var n = 1; n <= Members; n++)
        {
            var ticker = Ticker(n);

            await Seam.ExecuteAsync(
                """
                INSERT INTO security_daily (ticker, date, sector, size_bucket, market_cap, is_active)
                VALUES (@t, @d, @sector, @bucket, 1000000000, true)
                ON CONFLICT (ticker, date) DO UPDATE SET
                    is_active = true, sector = EXCLUDED.sector, size_bucket = EXCLUDED.size_bucket;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ticker);
                    c.Parameters.AddWithValue("d", Seam.From.AddDays(-28));
                    c.Parameters.AddWithValue("sector", n <= Members / 2 ? Thin : Rest);
                    c.Parameters.AddWithValue("bucket", Bucket);
                }, ct).ConfigureAwait(false);

            // One member carries the sessions the range walks.
            if (n == 1)
            {
                await Seam.ExecuteAsync(
                    """
                    INSERT INTO price_daily (ticker, date, open, high, low, close, adj_close, volume)
                    SELECT @t, d::date, 10, 11, 9, 10, 10, 100000
                    FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
                    ON CONFLICT (ticker, date) DO NOTHING;
                    """,
                    c =>
                    {
                        c.Parameters.AddWithValue("t", ticker);
                        c.Parameters.AddWithValue("f", Seam.From);
                        c.Parameters.AddWithValue("s", Seam.To);
                    }, ct).ConfigureAwait(false);
            }

            // **The ORDER has to move with the date, not merely the value.** A percentile
            // is a rank, so a metric that grows with the date while keeping the same
            // ordering across tickers ranks identically on every date. The first version
            // of this fixture did exactly that and the distinctness assertion caught it.
            // Rotating the value by the date offset permutes the ordering instead, so a
            // range path that ranked one date and wrote it to all five cannot pass.
            await Seam.ExecuteAsync(
                """
                INSERT INTO indicator_daily (ticker, date, dist_200dma, atr_pct)
                SELECT @t, d::date,
                       (((@n + (d::date - @f::date)) % @m) * 0.01)::real,
                       (0.02 + @n * 0.001)::real
                FROM generate_series(@f::date, @s::date, INTERVAL '1 day') AS d
                ON CONFLICT (ticker, date) DO UPDATE SET
                    dist_200dma = EXCLUDED.dist_200dma, atr_pct = EXCLUDED.atr_pct;
                """,
                c =>
                {
                    c.Parameters.AddWithValue("t", ticker);
                    c.Parameters.AddWithValue("n", n);
                    c.Parameters.AddWithValue("m", Members);
                    c.Parameters.AddWithValue("f", Seam.From);
                    c.Parameters.AddWithValue("s", Seam.To);
                }, ct).ConfigureAwait(false);
        }
    }
}
