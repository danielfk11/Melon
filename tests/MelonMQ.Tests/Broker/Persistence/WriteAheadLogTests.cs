using FluentAssertions;
using MelonMQ.Broker.Persistence;
using System.IO;
using Xunit;

namespace MelonMQ.Tests.Broker.Persistence;

public class WriteAheadLogTests : IDisposable
{
    private readonly string _tempDirectory;

    public WriteAheadLogTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"melonmq-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task AppendAsync_ShouldWriteRecord()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 1024);
        var data = "Test message"u8.ToArray();

        // Act
        var offset = await wal.AppendAsync(WalRecordType.Message, data);

        // Assert
        offset.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReadAsync_ShouldReturnWrittenRecord()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 1024);
        var data = "Test message content"u8.ToArray();
        var offset = await wal.AppendAsync(WalRecordType.Message, data);

        // Act
        var record = await wal.ReadAsync(offset);

        // Assert
        record.Should().NotBeNull();
        record!.Type.Should().Be(WalRecordType.Message);
        record.Data.Should().Equal(data);
    }

    [Fact]
    public async Task ReadAsync_WithInvalidOffset_ShouldReturnNull()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 1024);

        // Act
        var record = await wal.ReadAsync(999999);

        // Assert
        record.Should().BeNull();
    }

    [Fact]
    public async Task RecoverAsync_ShouldRestoreRecords()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 1024);
        var messages = new[]
        {
            "Message 1"u8.ToArray(),
            "Message 2"u8.ToArray(),
            "Message 3"u8.ToArray()
        };

        // Write records
        var offsets = new List<long>();
        foreach (var message in messages)
        {
            var offset = await wal.AppendAsync(WalRecordType.Message, message);
            offsets.Add(offset);
        }

        await wal.SyncAsync();
        wal.Dispose();

        // Act - Create new WAL instance and recover
        var newWal = new SegmentedWal(_tempDirectory, maxSegmentSize: 1024);
        var recoveredRecords = new List<(long Offset, WalRecord Record)>();
        
        await foreach (var (offset, record) in newWal.RecoverAsync())
        {
            recoveredRecords.Add((offset, record));
        }

        // Assert
        recoveredRecords.Should().HaveCount(3);
        
        for (int i = 0; i < messages.Length; i++)
        {
            recoveredRecords[i].Offset.Should().Be(offsets[i]);
            recoveredRecords[i].Record.Type.Should().Be(WalRecordType.Message);
            recoveredRecords[i].Record.Data.Should().Equal(messages[i]);
        }

        newWal.Dispose();
    }

    [Fact]
    public async Task AppendAsync_ExceedingSegmentSize_ShouldCreateNewSegment()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 100); // Small segment size
        var largeData = new byte[150]; // Larger than segment size
        Array.Fill(largeData, (byte)'X');

        // Act
        var offset1 = await wal.AppendAsync(WalRecordType.Message, "Small message"u8.ToArray());
        var offset2 = await wal.AppendAsync(WalRecordType.Message, largeData);

        // Assert
        offset1.Should().BeGreaterThan(0);
        offset2.Should().BeGreaterThan(offset1);

        // Should be able to read both records
        var record1 = await wal.ReadAsync(offset1);
        var record2 = await wal.ReadAsync(offset2);

        record1.Should().NotBeNull();
        record2.Should().NotBeNull();
        record2!.Data.Should().Equal(largeData);
    }

    [Fact]
    public async Task SyncAsync_ShouldPersistData()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, syncPolicy: WalSyncPolicy.Never);
        var data = "Test data for sync"u8.ToArray();
        var offset = await wal.AppendAsync(WalRecordType.Message, data);

        // Act
        await wal.SyncAsync();

        // Assert
        // Verify data is on disk by creating new instance
        wal.Dispose();
        var newWal = new SegmentedWal(_tempDirectory);
        var record = await newWal.ReadAsync(offset);
        
        record.Should().NotBeNull();
        record!.Data.Should().Equal(data);
        
        newWal.Dispose();
    }

    [Fact]
    public async Task TruncateAsync_ShouldRemoveRecordsAfterOffset()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 1024);
        
        var offset1 = await wal.AppendAsync(WalRecordType.Message, "Message 1"u8.ToArray());
        var offset2 = await wal.AppendAsync(WalRecordType.Message, "Message 2"u8.ToArray());
        var offset3 = await wal.AppendAsync(WalRecordType.Message, "Message 3"u8.ToArray());

        // Act
        await wal.TruncateAsync(offset2);

        // Assert
        var record1 = await wal.ReadAsync(offset1);
        var record2 = await wal.ReadAsync(offset2);
        var record3 = await wal.ReadAsync(offset3);

        record1.Should().NotBeNull(); // Should exist
        record2.Should().NotBeNull(); // Should exist (at truncate point)
        record3.Should().BeNull();    // Should be removed
    }

    [Fact]
    public async Task CompactAsync_ShouldRemoveUnneededSegments()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 50);
        
        // Write enough data to create multiple segments
        var offsets = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            var offset = await wal.AppendAsync(WalRecordType.Message, System.Text.Encoding.UTF8.GetBytes($"Message {i}"));
            offsets.Add(offset);
        }

        await wal.SyncAsync();
        var segmentsBefore = Directory.GetFiles(_tempDirectory, "*.wal").Length;

        // Act
        await wal.CompactAsync(offsets[7]); // Keep only last few records

        // Assert
        var segmentsAfter = Directory.GetFiles(_tempDirectory, "*.wal").Length;
        segmentsAfter.Should().BeLessThan(segmentsBefore);

        // Should still be able to read records after compaction point
        var record8 = await wal.ReadAsync(offsets[8]);
        var record9 = await wal.ReadAsync(offsets[9]);
        
        record8.Should().NotBeNull();
        record9.Should().NotBeNull();

        // Records before compaction point should be gone
        var record0 = await wal.ReadAsync(offsets[0]);
        record0.Should().BeNull();
    }

    [Theory]
    [InlineData(WalSyncPolicy.Never)]
    [InlineData(WalSyncPolicy.Batch)]
    [InlineData(WalSyncPolicy.Always)]
    public async Task DifferentSyncPolicies_ShouldWork(WalSyncPolicy syncPolicy)
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, syncPolicy: syncPolicy);
        var data = System.Text.Encoding.UTF8.GetBytes($"Test data for {syncPolicy}");

        // Act
        var offset = await wal.AppendAsync(WalRecordType.Message, data);

        // Assert
        var record = await wal.ReadAsync(offset);
        record.Should().NotBeNull();
        record!.Data.Should().Equal(data);
    }

    [Fact]
    public async Task ConcurrentWrites_ShouldBeSerialized()
    {
        // Arrange
        var wal = new SegmentedWal(_tempDirectory, maxSegmentSize: 1024);
        var tasks = new List<Task<long>>();

        // Act - Concurrent writes
        for (int i = 0; i < 10; i++)
        {
            var messageData = System.Text.Encoding.UTF8.GetBytes($"Concurrent message {i}");
            tasks.Add(wal.AppendAsync(WalRecordType.Message, messageData));
        }

        var offsets = await Task.WhenAll(tasks);

        // Assert
        offsets.Should().HaveCount(10);
        offsets.Should().OnlyHaveUniqueItems(); // All offsets should be unique
        
        // Should be able to read all records
        for (int i = 0; i < offsets.Length; i++)
        {
            var record = await wal.ReadAsync(offsets[i]);
            record.Should().NotBeNull();
            record!.Type.Should().Be(WalRecordType.Message);
        }
    }
}