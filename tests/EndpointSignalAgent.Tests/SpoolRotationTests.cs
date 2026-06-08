using EndpointSignalAgent.SignalCollection.Services;
using Xunit;

namespace EndpointSignalAgent.Tests;

public sealed class SpoolRotationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _spoolPath;
    private readonly string _bakPath;

    public SpoolRotationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"spool_rot_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _spoolPath = Path.Combine(_dir, "signals.jsonl");
        _bakPath = _spoolPath + ".bak";
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void RotateIfNeeded_DoesNotRotate_WhenFileBelowThreshold()
    {
        File.WriteAllText(_spoolPath, "small content");
        SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1_000_000);
        Assert.True(File.Exists(_spoolPath));
        Assert.False(File.Exists(_bakPath));
    }

    [Fact]
    public void RotateIfNeeded_RenamesFile_WhenFileExceedsThreshold()
    {
        File.WriteAllText(_spoolPath, "any content");
        SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1);
        Assert.False(File.Exists(_spoolPath));
        Assert.True(File.Exists(_bakPath));
    }

    [Fact]
    public void RotateIfNeeded_OverwritesPreviousBackup()
    {
        File.WriteAllText(_bakPath, "old backup");
        File.WriteAllText(_spoolPath, "new content");
        SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1);
        Assert.Equal("new content", File.ReadAllText(_bakPath));
    }

    [Fact]
    public void RotateIfNeeded_DoesNotThrow_WhenFileDoesNotExist()
    {
        var ex = Record.Exception(() =>
            SpoolRotation.RotateIfNeeded(_spoolPath, thresholdBytes: 1_000_000));
        Assert.Null(ex);
    }
}
