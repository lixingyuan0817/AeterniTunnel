using Aeterni.Tunnel.Engine.Logging;

namespace Aeterni.Tunnel.Engine.Tests;

public class LogLineParserTests
{
    [Theory]
    [InlineData("[I] [service.go:298] login to server success", LogLevel.Success)]
    [InlineData("[I] [proxy.go:120] start proxy success", LogLevel.Success)]
    [InlineData("[W] [proxy.go:88] proxy register failed", LogLevel.Warn)]
    [InlineData("[E] [control.go:45] connection refused", LogLevel.Error)]
    [InlineData("[D] [xlog.go:10] debug detail", LogLevel.Debug)]
    public void Parse_Levels(string line, LogLevel expected)
    {
        Assert.Equal(expected, LogLineParser.Parse(line).Level);
    }

    [Fact]
    public void Parse_PlainLine_IsInfo()
    {
        var entry = LogLineParser.Parse("some plain output without level");
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal("some plain output without level", entry.Message);
    }

    [Fact]
    public void Parse_StripsLevelPrefix()
    {
        var entry = LogLineParser.Parse("2025-01-01 10:00:00 [W] bind port failed");
        Assert.Equal(LogLevel.Warn, entry.Level);
        Assert.Equal("bind port failed", entry.Message);
    }
}

public class RingBufferTests
{
    [Fact]
    public void Add_KeepsCapacity()
    {
        var buf = new RingBuffer<int>(3);
        for (var i = 0; i < 10; i++) buf.Add(i);
        Assert.Equal(3, buf.Count);
        Assert.Equal(new[] { 7, 8, 9 }, buf.Snapshot());
    }

    [Fact]
    public void Snapshot_OrderPreserved()
    {
        var buf = new RingBuffer<string>(10);
        buf.Add("a"); buf.Add("b"); buf.Add("c");
        Assert.Equal(new[] { "a", "b", "c" }, buf.Snapshot());
    }
}

public class FileLoggerTests
{
    [Fact]
    public void Write_CreatesFileWithLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "at-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var logger = new FileLogger(path))
            {
                logger.Info("hello");
                logger.Error("boom");
            }

            var content = File.ReadAllText(path);
            Assert.Contains("hello", content);
            Assert.Contains("[E] boom", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LevelFilter_SuppressesBelowMin()
    {
        var path = Path.Combine(Path.GetTempPath(), "at-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var logger = new FileLogger(path, LogLevel.Warn))
            {
                logger.Info("noise");
                logger.Warn("warning");
            }

            var content = File.ReadAllText(path);
            Assert.DoesNotContain("noise", content);
            Assert.Contains("warning", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Roll_WhenExceedsMaxSize()
    {
        var path = Path.Combine(Path.GetTempPath(), "at-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var logger = new FileLogger(path, LogLevel.Debug, 2048))
            {
                for (var i = 0; i < 200; i++)
                    logger.Info(new string('x', 100));
            }

            Assert.True(File.Exists(path + ".1") || new FileInfo(path).Length < 2 * 2048,
                "滚动应发生（.1 存在）或主文件未超限");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".1");
        }
    }

    [Fact]
    public void ParseLevel_MapsStrings()
    {
        Assert.Equal(LogLevel.Debug, FileLogger.ParseLevel("debug"));
        Assert.Equal(LogLevel.Warn, FileLogger.ParseLevel("warning"));
        Assert.Equal(LogLevel.Error, FileLogger.ParseLevel("error"));
        Assert.Equal(LogLevel.Info, FileLogger.ParseLevel("unknown"));
    }
}
