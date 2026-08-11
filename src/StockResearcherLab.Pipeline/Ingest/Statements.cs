using System.Globalization;
using System.Text.Json;

namespace StockResearcherLab.Pipeline.Ingest;

/// <summary>One fiscal period, merged across the three statements.</summary>
public sealed class Statement
{
    private readonly Dictionary<string, decimal?> _values = new(StringComparer.Ordinal);

    public required DateOnly PeriodEnd { get; init; }

    public DateOnly? FilingDate { get; set; }

    /// <summary>
    /// Which statement supplied <see cref="FilingDate"/>. Recorded because the
    /// probe found the balance sheet and the income statement disagreeing on NVDA
    /// in 2 of 109 periods, and a disagreement resolved silently is a date nobody
    /// can trace.
    /// </summary>
    public string? FilingDateSource { get; set; }

    public decimal? Value(string column) => _values.GetValueOrDefault(column);

    public void Set(string column, decimal? value)
    {
        // First writer wins, and the order is balance sheet then income then cash
        // flow. netIncome appears on two statements and they can differ.
        if (value is not null && !_values.ContainsKey(column))
        {
            _values[column] = value;
        }
    }
}

/// <summary>
/// Parses <c>Financials</c> into one <see cref="Statement"/> per fiscal period.
///
/// Only the columns <c>0002</c> created are taken. The balance sheet alone carries
/// about sixty fields per period and ingesting all of them would be storing data
/// because it is there rather than because anything reads it.
/// </summary>
public static class Statements
{
    /// <summary>Provider field to column, per statement. Ordered so the balance sheet wins a tie.</summary>
    private static readonly (string Block, (string Field, string Column)[] Map)[] Blocks =
    [
        ("Balance_Sheet",
        [
            ("totalAssets", "total_assets"),
            ("totalLiab", "total_liab"),
            ("totalStockholderEquity", "total_stockholder_equity"),
            ("cash", "cash"),
            ("cashAndEquivalents", "cash_and_equivalents"),
            ("shortTermInvestments", "short_term_investments"),
            ("netDebt", "net_debt"),
            ("shortLongTermDebtTotal", "short_long_term_debt_total"),
            ("longTermDebt", "long_term_debt"),
            ("inventory", "inventory"),
            ("netReceivables", "net_receivables"),
            ("accountsPayable", "accounts_payable"),
            ("totalCurrentAssets", "total_current_assets"),
            ("totalCurrentLiabilities", "total_current_liabilities"),
            ("propertyPlantAndEquipmentNet", "property_plant_equipment_net"),
            ("goodWill", "goodwill"),
            ("intangibleAssets", "intangible_assets"),
            ("commonStockSharesOutstanding", "shares_outstanding"),
        ]),
        ("Income_Statement",
        [
            ("totalRevenue", "total_revenue"),
            ("costOfRevenue", "cost_of_revenue"),
            ("grossProfit", "gross_profit"),
            ("operatingIncome", "operating_income"),
            ("ebit", "ebit"),
            ("ebitda", "ebitda"),
            ("netIncome", "net_income"),
            ("incomeBeforeTax", "income_before_tax"),
            ("incomeTaxExpense", "income_tax_expense"),
            ("interestExpense", "interest_expense"),
            ("researchDevelopment", "research_development"),
        ]),
        ("Cash_Flow",
        [
            ("totalCashFromOperatingActivities", "cash_from_operating"),
            ("totalCashflowsFromInvestingActivities", "cash_from_investing"),
            ("totalCashFromFinancingActivities", "cash_from_financing"),
            // Capital expenditure on its own line, so free cash flow is cash from
            // operating less capex rather than less all investing. Total investing
            // reads an acquisition as capex and an asset sale as free cash flow,
            // on S1's first ranking input [D-79].
            ("capitalExpenditures", "capital_expenditures"),
            ("depreciation", "depreciation"),
            ("dividendsPaid", "dividends_paid"),
            ("salePurchaseOfStock", "sale_purchase_of_stock"),
        ]),
    ];

    public static IReadOnlyList<Statement> Parse(JsonElement financials)
    {
        // A ticker the endpoint carries no financials for answers with a bare
        // string rather than an empty object, so the shape is checked before it is
        // walked. This is the ordinary case for an index, a fund or a recent
        // listing, and the universe excludes those anyway; it is not a reason to
        // fail the night.
        if (financials.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var byPeriod = new Dictionary<DateOnly, Statement>();

        foreach (var (block, map) in Blocks)
        {
            if (!financials.TryGetProperty(block, out var statement)
                || statement.ValueKind != JsonValueKind.Object
                || !statement.TryGetProperty("quarterly", out var quarterly)
                || quarterly.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var period in quarterly.EnumerateObject())
            {
                if (!DateOnly.TryParseExact(period.Name, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                {
                    continue;
                }

                if (!byPeriod.TryGetValue(end, out var s))
                {
                    byPeriod[end] = s = new Statement { PeriodEnd = end };
                }

                // The filing date, taken from the first statement that carries one.
                // Which one is recorded rather than resolved silently.
                if (s.FilingDate is null && Date(period.Value, "filing_date") is DateOnly filed)
                {
                    s.FilingDate = filed;
                    s.FilingDateSource = block;
                }

                foreach (var (field, column) in map)
                {
                    s.Set(column, Money(period.Value, field));
                }
            }
        }

        // Ordinal by period end, so two runs produce the same order.
        return byPeriod.Values.OrderBy(s => s.PeriodEnd).ToList();
    }

    private static DateOnly? Date(JsonElement row, string name)
        => row.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
           && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd",
               CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>
    /// Money as decimal, never float or double [INVARIANT 16]. The provider sends
    /// these as JSON numbers and sometimes as strings, and absent stays null rather
    /// than becoming zero: zero is a real value on every one of these fields.
    /// </summary>
    private static decimal? Money(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(
                v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }
}
