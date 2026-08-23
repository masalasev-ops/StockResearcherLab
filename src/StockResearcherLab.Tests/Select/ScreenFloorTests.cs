using System.Globalization;
using Npgsql;
using StockResearcherLab.Core;
using StockResearcherLab.Core.Stages;
using StockResearcherLab.Data;
using StockResearcherLab.Pipeline.Select;
using StockResearcherLab.Tests.Corpus;
using Xunit;

namespace StockResearcherLab.Tests.Select;

/// <summary>
/// Checkpoint 4.5. The floor, and the rank that carries it.
///
/// **Ranking here rather than in the allocator is what makes §06's "floors already
/// applied" literally true.** C14 then needs no floor knowledge at all and cannot apply
/// one differently.
/// </summary>
[Collection("database")]
public sealed class ScreenFloorTests
{
    private const string ScreenId = "SRLTESTF";
    private const int Lookback = 5;

    private static readonly DateOnly RunDate = new(2021, 3, 10);

    /// <summary>
    /// **The assertion D-115 exists for.** A hundred scores of which forty are null:
    /// the p98 over the sixty real ones is a different number from the p98 a
    /// null-counting denominator would give, and both are stated.
    ///
    /// Scores 1 to 60 on the real names. p98 over sixty values by linear interpolation
    /// is 1 + 0.98 * 59 = 58.82. A denominator counting the forty nulls would place the
    /// 98th percentile far higher up the same sixty values, because nulls sort last in
    /// Postgres and the aggregate would be reaching into them.
    /// </summary>
    [Fact]
    public async Task ThePercentileIsOverTheNonNullPopulation()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        try
        {
            await SeedScoresAsync(RunDate, Enumerable.Range(1, 60).Select(i => (double?) i)
                .Concat(Enumerable.Repeat((double?) null, 40)), ct);

            var (days, p98) = await TrailingAsync(RunDate, lookbackDays: 1, ct);

            Assert.Equal(1, days);
            Assert.Equal(58.82d, p98!.Value, 2);

            // What a null-counting denominator would give, stated so the two are visibly
            // different rather than asserted to be equal to the right one alone.
            Assert.NotEqual(98.0d, p98.Value, 2);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// Below the lookback there is no floor, <c>observation_days</c> records the short
    /// window, and nothing is ranked [D-115, `SCREEN_LIFECYCLE.md` §5.1].
    /// </summary>
    [Fact]
    public async Task BelowTheLookbackThereIsNoFloorAndNothingIsRanked()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        try
        {
            // Three dates against a lookback of five.
            for (var i = 2; i >= 0; i--)
            {
                await SeedScoresAsync(RunDate.AddDays(-i), Enumerable.Range(1, 50).Select(x => (double?) x), ct);
            }

            await RunFloorAsync(RunDate, ct);

            var history = await HistoryAsync(RunDate, ct);

            Assert.Equal(3, history.ObservationDays);
            Assert.Null(history.FloorScore);
            Assert.NotNull(history.P98Trailing);

            Assert.Equal(0L, await RankedCountAsync(RunDate, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// At the lookback a floor exists, the rows at or above it are ranked dense from 1,
    /// and everything below carries null.
    /// </summary>
    [Fact]
    public async Task AtTheLookbackTheFloorRanksTheTopOfTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        try
        {
            for (var i = Lookback - 1; i >= 0; i--)
            {
                await SeedScoresAsync(RunDate.AddDays(-i), Enumerable.Range(1, 100).Select(x => (double?) x), ct);
            }

            await RunFloorAsync(RunDate, ct);

            var history = await HistoryAsync(RunDate, ct);

            Assert.Equal(Lookback, history.ObservationDays);
            Assert.NotNull(history.FloorScore);

            var ranked = await RankedCountAsync(RunDate, ct);

            Assert.True(ranked > 0, "a floor exists, so something at or above it must be ranked");

            // Dense from 1, and the top score takes rank 1.
            Assert.Equal(1, await RankOfScoreAsync(RunDate, 100, ct));

            // Everything below the floor carries null, so the ranked set and the scored
            // set are different sizes by construction.
            Assert.Null(await RankOfScoreAsync(RunDate, 1, ct));
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>
    /// **A screen where nothing clears its floor returns nothing and the run does not
    /// fail** [`ARCHITECTURE.html` §18]. Every score on the date is null, so the floor
    /// stands from the trailing window and no row is ranked.
    /// </summary>
    [Fact]
    public async Task AScreenWhereNothingClearsItsFloorDoesNotFailTheRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await ClearAsync(ct);

        try
        {
            for (var i = Lookback; i >= 1; i--)
            {
                await SeedScoresAsync(RunDate.AddDays(-i), Enumerable.Range(1, 100).Select(x => (double?) x), ct);
            }

            // The date itself carries scores that are all null.
            await SeedScoresAsync(RunDate, Enumerable.Repeat((double?) null, 100), ct);

            await RunFloorAsync(RunDate, ct);

            Assert.Equal(0L, await RankedCountAsync(RunDate, ct));

            var history = await HistoryAsync(RunDate, ct);
            Assert.NotNull(history.FloorScore);
        }
        finally
        {
            await ClearAsync(ct);
        }
    }

    /// <summary>Ties take the same rank, so the order two equal names arrive in cannot matter.</summary>
    [Fact]
    public void TheRankIsDenseAndTheTieBreakIsExplicit()
    {
        var sql = ScreenEngine.RankSql("SX", RunDate, 50d);

        Assert.Contains("dense_rank() OVER (ORDER BY score DESC, ticker ASC)", sql, StringComparison.Ordinal);
        Assert.Contains("score >= 50", sql, StringComparison.Ordinal);
    }

    /// <summary>The trailing statement excludes nulls in the predicate, not only by the aggregate.</summary>
    [Fact]
    public void TheTrailingStatementNamesTheNonNullPopulation()
    {
        var sql = ScreenEngine.TrailingSql("SX", RunDate, 250, 98d);

        Assert.Contains("WHERE score IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("percentile_cont(0.98)", sql, StringComparison.Ordinal);
        Assert.Contains("count(DISTINCT date)", sql, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ plumbing ---

    private sealed record History(double? FloorScore, double? P98Trailing, int ObservationDays);

    private static async Task RunFloorAsync(DateOnly date, CancellationToken ct)
    {
        var stage = new ScreenEngine();
        var data = new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage));

        var rows = await data.ReadAsync(
            "screen_score_daily", ScreenEngine.TrailingSql(ScreenId, date, Lookback, 98d), ct)
            .ConfigureAwait(true);

        var days = Convert.ToInt32(rows[0][0], CultureInfo.InvariantCulture);
        var p98 = rows[0][1] is null or DBNull
            ? (double?) null
            : Convert.ToDouble(rows[0][1], CultureInfo.InvariantCulture);

        var floor = days >= Lookback ? p98 : null;

        await data.WriteAsync(
            "screen_history", WriteOperation.Insert,
            ScreenEngine.HistorySql(ScreenId, date, floor, p98, days), parameters: null, ct)
            .ConfigureAwait(true);

        if (floor is not null)
        {
            await data.WriteAsync(
                "screen_score_daily", WriteOperation.Update,
                ScreenEngine.RankSql(ScreenId, date, floor.Value), parameters: null, ct).ConfigureAwait(true);
        }
    }

    private static async Task<(int Days, double? P98)> TrailingAsync(
        DateOnly date, int lookbackDays, CancellationToken ct)
    {
        var stage = new ScreenEngine();
        var data = new StageData(TestDatabase.ConnectionString, new DeclaredAccess(stage));

        var rows = await data.ReadAsync(
            "screen_score_daily", ScreenEngine.TrailingSql(ScreenId, date, lookbackDays, 98d), ct)
            .ConfigureAwait(true);

        return (Convert.ToInt32(rows[0][0], CultureInfo.InvariantCulture),
            rows[0][1] is null or DBNull ? null : Convert.ToDouble(rows[0][1], CultureInfo.InvariantCulture));
    }

    private static async Task SeedScoresAsync(DateOnly date, IEnumerable<double?> scores, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        var i = 0;
        foreach (var score in scores)
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO screen_score_daily (date, screen_id, ticker, score, config_version)
                VALUES (@d, @s, @t, @v, 1)
                ON CONFLICT (date, screen_id, ticker) DO UPDATE SET score = EXCLUDED.score;
                """, conn);

            cmd.Parameters.AddWithValue("d", date);
            cmd.Parameters.AddWithValue("s", ScreenId);
            cmd.Parameters.AddWithValue("t", $"SRLTESTF.{i.ToString("D4", CultureInfo.InvariantCulture)}");
            cmd.Parameters.AddWithValue("v", (object?) (float?) score ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
            i++;
        }
    }

    private static async Task<History> HistoryAsync(DateOnly date, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT floor_score, p98_trailing, observation_days FROM screen_history WHERE screen_id = @s AND date = @d;",
            conn);

        cmd.Parameters.AddWithValue("s", ScreenId);
        cmd.Parameters.AddWithValue("d", date);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(ct).ConfigureAwait(true), "screen_history has no row for the date");

        return new History(
            reader.IsDBNull(0) ? null : reader.GetDouble(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.GetInt32(2));
    }

    private static async Task<long> RankedCountAsync(DateOnly date, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM screen_score_daily WHERE screen_id = @s AND date = @d AND rank_within_screen IS NOT NULL;",
            conn);

        cmd.Parameters.AddWithValue("s", ScreenId);
        cmd.Parameters.AddWithValue("d", date);

        return (long) (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true))!;
    }

    private static async Task<int?> RankOfScoreAsync(DateOnly date, double score, CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);
        await using var cmd = new NpgsqlCommand(
            "SELECT rank_within_screen FROM screen_score_daily WHERE screen_id = @s AND date = @d AND score = @v;",
            conn);

        cmd.Parameters.AddWithValue("s", ScreenId);
        cmd.Parameters.AddWithValue("d", date);
        cmd.Parameters.AddWithValue("v", (float) score);

        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(true);

        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task ClearAsync(CancellationToken ct)
    {
        await using var conn = await TestDatabase.OpenAsync(ct).ConfigureAwait(true);

        foreach (var sql in new[]
                 {
                     "DELETE FROM screen_score_daily WHERE screen_id = @s;",
                     "DELETE FROM screen_history WHERE screen_id = @s;",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("s", ScreenId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(true);
        }
    }
}
