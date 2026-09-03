namespace TotallyHot.ArcRouter.Gui.Console.Tests;

/// <summary>Covers <see cref="LogBuffer"/>: the bounded FIFO buffer backing the Console viewport.</summary>
public class LogBufferTests
{
    private static LogLineDto Line(string message, string level = "INFO") =>
        new(DateTimeOffset.UtcNow, level, message);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveCapacity_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogBuffer(capacity));
    }

    [Fact]
    public void Add_Null_Throws()
    {
        var buffer = new LogBuffer();

        Assert.Throws<ArgumentNullException>(() => buffer.Add(null!));
    }

    [Fact]
    public void Snapshot_Empty_ReturnsEmpty()
    {
        var buffer = new LogBuffer();

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void Add_WithinCapacity_PreservesInsertionOrder()
    {
        var buffer = new LogBuffer(capacity: 10);
        buffer.Add(Line("first"));
        buffer.Add(Line("second"));
        buffer.Add(Line("third"));

        var snapshot = buffer.Snapshot();

        Assert.Equal(["first", "second", "third"], snapshot.Select(l => l.Message));
    }

    [Fact]
    public void Add_BeyondCapacity_DropsOldestLines()
    {
        var buffer = new LogBuffer(capacity: 2);
        buffer.Add(Line("first"));
        buffer.Add(Line("second"));
        buffer.Add(Line("third"));

        var snapshot = buffer.Snapshot();

        Assert.Equal(["second", "third"], snapshot.Select(l => l.Message));
    }

    [Fact]
    public void Clear_RemovesAllLines()
    {
        var buffer = new LogBuffer();
        buffer.Add(Line("first"));
        buffer.Add(Line("second"));

        buffer.Clear();

        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void Clear_ThenAdd_StartsFreshWithinCapacity()
    {
        var buffer = new LogBuffer(capacity: 2);
        buffer.Add(Line("first"));
        buffer.Clear();
        buffer.Add(Line("second"));
        buffer.Add(Line("third"));
        buffer.Add(Line("fourth"));

        var snapshot = buffer.Snapshot();

        Assert.Equal(["third", "fourth"], snapshot.Select(l => l.Message));
    }

    [Fact]
    public void Snapshot_ReflectsCapacityAsPublicProperty()
    {
        var buffer = new LogBuffer(capacity: 42);

        Assert.Equal(42, buffer.Capacity);
    }
}

