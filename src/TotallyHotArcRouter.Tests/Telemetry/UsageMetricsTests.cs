using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>
/// Covers <see cref="UsageMetrics"/>'s instrument publication.
/// </summary>
/// <remarks>
/// Every instrument here is a process-wide static that <c>ProxyMiddleware</c> also writes to, and a
/// <see cref="MeterListener"/> subscribes to the *instrument*, not to the test that created it - so a
/// proxy test running in a parallel xUnit collection invokes these callbacks on its own thread. Each
/// test therefore tags its measurement with a unique provider value and ignores every measurement that
/// does not carry it (see <see cref="IsFromThisTest"/>). Without that filter these tests are racy in two
/// different ways: the accumulating ones silently count a foreign measurement into their total, and the
/// tag-collecting one throws "Collection was modified" when a foreign measurement appends to its list
/// mid-assertion. Filtering fixes both while keeping the assertions exact rather than "at least".
/// </remarks>
public class UsageMetricsTests
{
    /// <summary>
    /// A provider tag value unique to the calling test, so its measurements are distinguishable from any
    /// other test's on the same process-wide instrument.
    /// </summary>
    private static string NewProviderTag([CallerMemberName] string caller = "") =>
        $"{caller}-{Guid.NewGuid():N}";

    /// <summary>
    /// Whether a measurement's tags carry the provider value <see cref="NewProviderTag"/> minted for this
    /// test, i.e. whether this test - rather than something running beside it - recorded it.
    /// </summary>
    private static bool IsFromThisTest(ReadOnlySpan<KeyValuePair<string, object?>> tags, string provider)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "provider" && Equals(tag.Value, provider))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void ExtractionFailedTotal_Add_IsObservableThroughTheMeter()
    {
        var provider = NewProviderTag();
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.extraction_failed")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (IsFromThisTest(tags, provider))
            {
                observed += measurement;
            }
        });
        listener.Start();

        UsageMetrics.ExtractionFailedTotal.Add(1, new KeyValuePair<string, object?>("provider", provider));

        Assert.Equal(1, observed);
    }

    [Fact]
    public void TokensTotal_Add_TagsCarryThroughToTheListener()
    {
        var provider = NewProviderTag();
        var observedTags = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.tokens")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (IsFromThisTest(tags, provider))
            {
                observedTags.AddRange(tags.ToArray());
            }
        });
        listener.Start();

        UsageMetrics.TokensTotal.Add(
            100,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", "claude"),
            new KeyValuePair<string, object?>("kind", "prompt"));

        Assert.Contains(observedTags, t => t.Key == "kind" && Equals(t.Value, "prompt"));
    }

    [Fact]
    public void CostUsdTotal_Add_IsObservableThroughTheMeter()
    {
        var provider = NewProviderTag();
        double observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.cost_usd")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            if (IsFromThisTest(tags, provider))
            {
                observed += measurement;
            }
        });
        listener.Start();

        UsageMetrics.CostUsdTotal.Add(1.5, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("model", "gpt"));

        Assert.Equal(1.5, observed);
    }

    [Fact]
    public void UnpricedRequestsTotal_Add_IsObservableThroughTheMeter()
    {
        var provider = NewProviderTag();
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.unpriced_requests")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (IsFromThisTest(tags, provider))
            {
                observed += measurement;
            }
        });
        listener.Start();

        UsageMetrics.UnpricedRequestsTotal.Add(1, new KeyValuePair<string, object?>("provider", provider));

        Assert.Equal(1, observed);
    }
}
