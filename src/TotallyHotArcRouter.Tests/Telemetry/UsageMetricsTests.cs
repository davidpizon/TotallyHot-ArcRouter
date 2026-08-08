using System.Diagnostics.Metrics;
using TotallyHot.ArcRouter.Telemetry;

namespace TotallyHot.ArcRouter.Tests.Telemetry;

/// <summary>Covers <see cref="UsageMetrics"/>'s instrument publication.</summary>
public class UsageMetricsTests
{
    [Fact]
    public void ExtractionFailedTotal_Add_IsObservableThroughTheMeter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.extraction_failed")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed += measurement);
        listener.Start();

        UsageMetrics.ExtractionFailedTotal.Add(1, new KeyValuePair<string, object?>("provider", "openai"));

        Assert.Equal(1, observed);
    }

    [Fact]
    public void TokensTotal_Add_TagsCarryThroughToTheListener()
    {
        var observedTags = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.tokens")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => observedTags.AddRange(tags.ToArray()));
        listener.Start();

        UsageMetrics.TokensTotal.Add(
            100,
            new KeyValuePair<string, object?>("provider", "anthropic"),
            new KeyValuePair<string, object?>("model", "claude"),
            new KeyValuePair<string, object?>("kind", "prompt"));

        Assert.Contains(observedTags, t => t.Key == "kind" && Equals(t.Value, "prompt"));
    }

    [Fact]
    public void CostUsdTotal_Add_IsObservableThroughTheMeter()
    {
        double observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.cost_usd")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => observed += measurement);
        listener.Start();

        UsageMetrics.CostUsdTotal.Add(1.5, new KeyValuePair<string, object?>("provider", "openai"), new KeyValuePair<string, object?>("model", "gpt"));

        Assert.Equal(1.5, observed);
    }

    [Fact]
    public void UnpricedRequestsTotal_Add_IsObservableThroughTheMeter()
    {
        long observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UsageMetrics.MeterName && instrument.Name == "arcrouter.usage.unpriced_requests")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observed += measurement);
        listener.Start();

        UsageMetrics.UnpricedRequestsTotal.Add(1, new KeyValuePair<string, object?>("provider", "openai"));

        Assert.Equal(1, observed);
    }
}
