using FluentAssertions;
using MelonMQ.Common;
using Xunit;

namespace MelonMQ.Tests.Common;

public class TlvEncodingTests
{
    [Fact]
    public void WriteString_ShouldEncodeCorrectly()
    {
        // Arrange
        var buffer = new List<byte>();
        var value = "Hello World";

        // Act
        TlvEncoding.WriteString(buffer, value);

        // Assert
        buffer.Count.Should().Be(4 + value.Length); // 4 bytes for length + string bytes
        
        var length = BitConverter.ToInt32(buffer.Take(4).ToArray(), 0);
        length.Should().Be(value.Length);
        
        var decodedString = System.Text.Encoding.UTF8.GetString(buffer.Skip(4).ToArray());
        decodedString.Should().Be(value);
    }

    [Fact]
    public void ReadString_ShouldDecodeCorrectly()
    {
        // Arrange
        var value = "Test String";
        var buffer = new List<byte>();
        TlvEncoding.WriteString(buffer, value);
        
        var span = buffer.ToArray().AsSpan();

        // Act
        var (result, consumed) = TlvEncoding.ReadString(span);

        // Assert
        result.Should().Be(value);
        consumed.Should().Be(buffer.Count);
    }

    [Fact]
    public void WriteInt32_ShouldEncodeCorrectly()
    {
        // Arrange
        var buffer = new List<byte>();
        var value = 12345;

        // Act
        TlvEncoding.WriteInt32(buffer, value);

        // Assert
        buffer.Count.Should().Be(4);
        var decoded = BitConverter.ToInt32(buffer.ToArray(), 0);
        decoded.Should().Be(value);
    }

    [Fact]
    public void ReadInt32_ShouldDecodeCorrectly()
    {
        // Arrange
        var value = 67890;
        var buffer = new List<byte>();
        TlvEncoding.WriteInt32(buffer, value);
        
        var span = buffer.ToArray().AsSpan();

        // Act
        var (result, consumed) = TlvEncoding.ReadInt32(span);

        // Assert
        result.Should().Be(value);
        consumed.Should().Be(4);
    }

    [Fact]
    public void WriteBoolean_ShouldEncodeCorrectly()
    {
        // Arrange
        var buffer = new List<byte>();

        // Act
        TlvEncoding.WriteBoolean(buffer, true);
        TlvEncoding.WriteBoolean(buffer, false);

        // Assert
        buffer.Count.Should().Be(2);
        buffer[0].Should().Be(1);
        buffer[1].Should().Be(0);
    }

    [Fact]
    public void ReadBoolean_ShouldDecodeCorrectly()
    {
        // Arrange
        var buffer = new List<byte> { 1, 0 };
        var span = buffer.ToArray().AsSpan();

        // Act
        var (result1, consumed1) = TlvEncoding.ReadBoolean(span);
        var (result2, consumed2) = TlvEncoding.ReadBoolean(span.Slice(consumed1));

        // Assert
        result1.Should().BeTrue();
        consumed1.Should().Be(1);
        result2.Should().BeFalse();
        consumed2.Should().Be(1);
    }

    [Fact]
    public void WriteByteArray_ShouldEncodeCorrectly()
    {
        // Arrange
        var buffer = new List<byte>();
        var value = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        TlvEncoding.WriteByteArray(buffer, value);

        // Assert
        buffer.Count.Should().Be(4 + value.Length); // 4 bytes for length + array
        
        var length = BitConverter.ToInt32(buffer.Take(4).ToArray(), 0);
        length.Should().Be(value.Length);
        
        var decodedArray = buffer.Skip(4).ToArray();
        decodedArray.Should().Equal(value);
    }

    [Fact]
    public void ReadByteArray_ShouldDecodeCorrectly()
    {
        // Arrange
        var value = new byte[] { 10, 20, 30, 40, 50 };
        var buffer = new List<byte>();
        TlvEncoding.WriteByteArray(buffer, value);
        
        var span = buffer.ToArray().AsSpan();

        // Act
        var (result, consumed) = TlvEncoding.ReadByteArray(span);

        // Assert
        result.Should().Equal(value);
        consumed.Should().Be(buffer.Count);
    }

    [Fact]
    public void WriteDictionary_ShouldEncodeCorrectly()
    {
        // Arrange
        var buffer = new List<byte>();
        var dict = new Dictionary<string, object>
        {
            ["key1"] = "value1",
            ["key2"] = 42,
            ["key3"] = true
        };

        // Act
        TlvEncoding.WriteDictionary(buffer, dict);

        // Assert
        buffer.Count.Should().BeGreaterThan(0);
        
        // Should start with count
        var count = BitConverter.ToInt32(buffer.Take(4).ToArray(), 0);
        count.Should().Be(3);
    }

    [Fact]
    public void ReadDictionary_ShouldDecodeCorrectly()
    {
        // Arrange
        var original = new Dictionary<string, object>
        {
            ["test"] = "hello",
            ["number"] = 123,
            ["flag"] = false
        };
        
        var buffer = new List<byte>();
        TlvEncoding.WriteDictionary(buffer, original);
        
        var span = buffer.ToArray().AsSpan();

        // Act
        var (result, consumed) = TlvEncoding.ReadDictionary(span);

        // Assert
        result.Should().HaveCount(3);
        consumed.Should().Be(buffer.Count);
        
        result["test"].Should().Be("hello");
        result["number"].Should().Be(123);
        result["flag"].Should().Be(false);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("Hello, World!")]
    [InlineData("Unicode: 你好世界")]
    public void StringRoundTrip_ShouldPreserveValue(string value)
    {
        // Arrange
        var buffer = new List<byte>();

        // Act
        TlvEncoding.WriteString(buffer, value);
        var (result, _) = TlvEncoding.ReadString(buffer.ToArray().AsSpan());

        // Assert
        result.Should().Be(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Int32RoundTrip_ShouldPreserveValue(int value)
    {
        // Arrange
        var buffer = new List<byte>();

        // Act
        TlvEncoding.WriteInt32(buffer, value);
        var (result, _) = TlvEncoding.ReadInt32(buffer.ToArray().AsSpan());

        // Assert
        result.Should().Be(value);
    }
}