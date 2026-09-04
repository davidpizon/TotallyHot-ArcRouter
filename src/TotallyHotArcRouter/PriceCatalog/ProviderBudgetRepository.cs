using System.Globalization;

namespace TotallyHot.ArcRouter.PriceCatalog;

/// <summary>
/// A provider's persisted monthly budget caps, keyed on the provider key used in model-routing.json / the
/// <c>/admin</c> API. A <see langword="null"/> cap means "no budget for that dimension" - distinct from a
/// zero cap.
/// </summary>
/// <param name="ProviderKey">The provider key (e.g. <c>openai</c>).</param>
/// <param name="DollarCap">The monthly USD cap, or <see langword="null"/> for no dollar budget.</param>
/// <param name="TokenCap">The monthly total-token cap, or <see langword="null"/> for no token budget.</param>
/// <param name="WindowKind">
/// The persisted <see cref="BudgetWindow"/> discriminator ("Monthly", "Weekly", or "RollingHours") the cap
/// resets on. Always non-null on a read row; <see cref="PriceCatalogDatabase"/>'s migration backfills
/// existing rows to "Monthly", matching the only behavior that existed before this column did.
/// </param>
/// <param name="WindowHours">
/// The block length in hours when <paramref name="WindowKind"/> is "RollingHours"; otherwise
/// <see langword="null"/>.
/// </param>
public sealed record ProviderBudgetRow(
    string ProviderKey,
    decimal? DollarCap,
    long? TokenCap,
    string WindowKind = "Monthly",
    int? WindowHours = null);

/// <summary>
/// Thin ADO.NET wrapper over the provider-budget CRUD table (<c>provider_budgets</c>). Split out of the
/// former monolithic <c>PriceCatalogRepository</c> (docs/router/code-smell-refactoring-plan.md M3) - the
/// other five concerns that repository once mixed together each now have their own type in this namespace.
/// </summary>
public sealed class ProviderBudgetRepository : PriceCatalogRepositoryBase
{
    /// <summary>Initializes a new instance of the <see cref="ProviderBudgetRepository"/> class.</summary>
    /// <param name="database">The catalog database.</param>
    public ProviderBudgetRepository(PriceCatalogDatabase database)
        : base(database)
    {
    }

    /// <summary>
    /// Reads every provider that has a persisted budget row. Providers with no row (the default) are absent
    /// from the result rather than returned with null caps, so the caller can treat "no row" and "row with
    /// both caps null" identically as unbudgeted.
    /// </summary>
    public IReadOnlyList<ProviderBudgetRow> GetProviderBudgets()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT provider_key, dollar_cap, token_cap, window_kind, window_hours FROM provider_budgets;";

        var rows = new List<ProviderBudgetRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new ProviderBudgetRow(
                ProviderKey: reader.GetString(0),
                DollarCap: reader.IsDBNull(1)
                    ? null
                    : decimal.Parse(s: reader.GetString(1), provider: CultureInfo.InvariantCulture),
                TokenCap: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                WindowKind: reader.GetString(3),
                WindowHours: reader.IsDBNull(4) ? null : reader.GetInt32(4)));

        return rows;
    }

    /// <summary>
    /// Persists a provider's budget caps and reset window. A <see langword="null"/> cap clears that
    /// dimension; when both are null the row is deleted, so an unbudgeted provider leaves no stale row
    /// behind (in that case <paramref name="window"/> is ignored, since there is no cap left to reset).
    /// Decimals are written as invariant text (matching how money round-trips elsewhere) so no binary-float
    /// rounding enters a dollar cap.
    /// </summary>
    public void SetProviderBudget(string providerKey, decimal? dollarCap, long? tokenCap, BudgetWindow? window = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        if (dollarCap is null && tokenCap is null)
        {
            command.CommandText = "DELETE FROM provider_budgets WHERE provider_key = $key;";
            command.Parameters.AddWithValue(parameterName: "$key", value: providerKey);
            command.ExecuteNonQuery();
            return;
        }

        var (windowKind, windowHours) = BudgetWindowCodec.Encode(window ?? new BudgetWindow.Monthly());

        command.CommandText = """
                              INSERT INTO provider_budgets (provider_key, dollar_cap, token_cap, window_kind, window_hours)
                              VALUES ($key, $dollar, $token, $windowKind, $windowHours)
                              ON CONFLICT(provider_key) DO UPDATE SET
                                  dollar_cap   = excluded.dollar_cap,
                                  token_cap    = excluded.token_cap,
                                  window_kind  = excluded.window_kind,
                                  window_hours = excluded.window_hours;
                              """;
        command.Parameters.AddWithValue(parameterName: "$key", value: providerKey);
        command.Parameters.AddWithValue(parameterName: "$dollar",
            value: dollarCap?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$token", value: tokenCap ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(parameterName: "$windowKind", value: windowKind);
        command.Parameters.AddWithValue(parameterName: "$windowHours", value: windowHours ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }
}