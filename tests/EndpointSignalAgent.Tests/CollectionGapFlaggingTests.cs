using EndpointSignalAgent.FeatureExtraction.Services;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class CollectionGapFlaggingTests
{
    [Fact]
    public void WindowOverlapsGap_GapInsideWindow_ReturnsTrue()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var gapStart    = DateTimeOffset.Parse("2026-01-01T10:00:15Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T10:00:45Z");

        Assert.True(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            new[] { (gapStart, gapEnd) }));
    }

    [Fact]
    public void WindowOverlapsGap_GapBeforeWindow_ReturnsFalse()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var gapStart    = DateTimeOffset.Parse("2026-01-01T09:58:00Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T09:59:00Z");

        Assert.False(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            new[] { (gapStart, gapEnd) }));
    }

    [Fact]
    public void WindowOverlapsGap_GapStraddlesWindowStart_ReturnsTrue()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var gapStart    = DateTimeOffset.Parse("2026-01-01T09:59:30Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T10:00:30Z");

        Assert.True(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            new[] { (gapStart, gapEnd) }));
    }

    [Fact]
    public void WindowOverlapsGap_NoGaps_ReturnsFalse()
    {
        var windowStart = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var windowEnd   = DateTimeOffset.Parse("2026-01-01T10:01:00Z");

        Assert.False(FeatureExtractorService.WindowOverlapsGap(
            windowStart, windowEnd,
            Array.Empty<(DateTimeOffset, DateTimeOffset)>()));
    }

    [Fact]
    public void IsInWarmUp_WindowStartBeforeExpiry_ReturnsTrue()
    {
        var windowStart  = DateTimeOffset.Parse("2026-01-01T10:00:10Z");
        var warmUpUntil  = DateTimeOffset.Parse("2026-01-01T10:00:30Z");

        Assert.True(FeatureExtractorService.IsInWarmUp(windowStart, warmUpUntil));
    }

    [Fact]
    public void IsInWarmUp_WindowStartAfterExpiry_ReturnsFalse()
    {
        var windowStart  = DateTimeOffset.Parse("2026-01-01T10:01:00Z");
        var warmUpUntil  = DateTimeOffset.Parse("2026-01-01T10:00:30Z");

        Assert.False(FeatureExtractorService.IsInWarmUp(windowStart, warmUpUntil));
    }

    [Fact]
    public void AdvanceNextWindowPastGap_CurrentBeforeGapEnd_AdvancesToAlignedGapEnd()
    {
        var currentNext = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T18:00:02Z");

        var result = FeatureExtractorService.AdvanceNextWindowPastGap(currentNext, gapEnd, stepSec: 30);

        // AlignToStepUtc floors to nearest 30s boundary
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T18:00:00Z"), result);
    }

    [Fact]
    public void AdvanceNextWindowPastGap_CurrentAtGapEnd_Unchanged()
    {
        var currentNext = DateTimeOffset.Parse("2026-01-01T18:00:00Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T18:00:00Z");

        var result = FeatureExtractorService.AdvanceNextWindowPastGap(currentNext, gapEnd, stepSec: 30);

        Assert.Equal(currentNext, result);
    }

    [Fact]
    public void AdvanceNextWindowPastGap_CurrentAfterGapEnd_Unchanged()
    {
        var currentNext = DateTimeOffset.Parse("2026-01-01T18:01:00Z");
        var gapEnd      = DateTimeOffset.Parse("2026-01-01T18:00:00Z");

        var result = FeatureExtractorService.AdvanceNextWindowPastGap(currentNext, gapEnd, stepSec: 30);

        Assert.Equal(currentNext, result);
    }

    [Fact]
    public void AdvanceNextWindowPastGap_NullCurrent_ReturnsNull()
    {
        var gapEnd = DateTimeOffset.Parse("2026-01-01T18:00:00Z");

        var result = FeatureExtractorService.AdvanceNextWindowPastGap(null, gapEnd, stepSec: 30);

        Assert.Null(result);
    }
}
