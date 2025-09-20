using FluentAssertions;
using MelonMQ.Common;
using System.Buffers;
using Xunit;

namespace MelonMQ.Tests.Common;

public class FrameCodecTests
{
    [Fact]
    public void WriteFrame_ShouldWriteValidFrame()
    {
        // Arrange
        var frame = new ProtocolFrame(
            Type: FrameType.PublishMessage,
            Flags: 0x01,
            CorrelationId: 12345,
            Payload: "Hello World"u8.ToArray()
        );
        
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        FrameCodec.WriteFrame(buffer, frame);

        // Assert
        var result = buffer.WrittenSpan;
        result.Length.Should().Be(ProtocolConstants.FrameHeaderSize + frame.Payload.Length);
        
        // Check magic
        result[0].Should().Be(ProtocolConstants.Magic[0]);
        result[1].Should().Be(ProtocolConstants.Magic[1]);
        
        // Check version
        result[2].Should().Be(ProtocolConstants.Version);
        
        // Check type
        result[3].Should().Be((byte)FrameType.PublishMessage);
        
        // Check flags
        result[4].Should().Be(0x01);
        
        // Check length
        var length = BitConverter.ToUInt32(result.Slice(5, 4));
        length.Should().Be((uint)frame.Payload.Length);
        
        // Check correlation ID
        var correlationId = BitConverter.ToUInt64(result.Slice(9, 8));
        correlationId.Should().Be(12345UL);
        
        // Check payload
        result.Slice(17).ToArray().Should().Equal(frame.Payload);
    }

    [Fact]
    public void TryReadFrame_ShouldReadValidFrame()
    {
        // Arrange
        var originalFrame = new ProtocolFrame(
            Type: FrameType.ConsumeMessage,
            Flags: 0x02,
            CorrelationId: 67890,
            Payload: "Test Message"u8.ToArray()
        );
        
        var buffer = new ArrayBufferWriter<byte>();
        FrameCodec.WriteFrame(buffer, originalFrame);
        var data = buffer.WrittenMemory;

        // Act
        var result = FrameCodec.TryReadFrame(data, out var frame, out var consumed);

        // Assert
        result.Should().BeTrue();
        consumed.Should().Be(data.Length);
        
        frame.Type.Should().Be(FrameType.ConsumeMessage);
        frame.Flags.Should().Be(0x02);
        frame.CorrelationId.Should().Be(67890UL);
        frame.Payload.Should().Equal(originalFrame.Payload);
    }

    [Fact]
    public void TryReadFrame_WithInsufficientData_ShouldReturnFalse()
    {
        // Arrange
        var incompleteData = new byte[10]; // Less than header size

        // Act
        var result = FrameCodec.TryReadFrame(incompleteData, out _, out var consumed);

        // Assert
        result.Should().BeFalse();
        consumed.Should().Be(0);
    }

    [Fact]
    public void TryReadFrame_WithInvalidMagic_ShouldThrow()
    {
        // Arrange
        var invalidData = new byte[ProtocolConstants.FrameHeaderSize];
        invalidData[0] = 0xFF; // Invalid magic

        // Act & Assert
        var act = () => FrameCodec.TryReadFrame(invalidData, out _, out _);
        act.Should().Throw<ProtocolException>()
            .WithMessage("Invalid magic bytes");
    }

    [Fact]
    public void TryReadFrame_WithUnsupportedVersion_ShouldThrow()
    {
        // Arrange
        var invalidData = new byte[ProtocolConstants.FrameHeaderSize];
        invalidData[0] = ProtocolConstants.Magic[0];
        invalidData[1] = ProtocolConstants.Magic[1];
        invalidData[2] = 99; // Unsupported version

        // Act & Assert
        var act = () => FrameCodec.TryReadFrame(invalidData, out _, out _);
        act.Should().Throw<ProtocolException>()
            .WithMessage("Unsupported protocol version: 99");
    }

    [Theory]
    [InlineData(FrameType.AuthRequest)]
    [InlineData(FrameType.PublishMessage)]
    [InlineData(FrameType.ConsumeMessage)]
    [InlineData(FrameType.AckMessage)]
    public void RoundTrip_ShouldPreserveFrameData(FrameType frameType)
    {
        // Arrange
        var originalFrame = new ProtocolFrame(
            Type: frameType,
            Flags: 0x05,
            CorrelationId: 999999,
            Payload: System.Text.Encoding.UTF8.GetBytes($"Frame type {frameType} payload")
        );

        // Act
        var buffer = new ArrayBufferWriter<byte>();
        FrameCodec.WriteFrame(buffer, originalFrame);
        
        var success = FrameCodec.TryReadFrame(buffer.WrittenMemory, out var decodedFrame, out _);

        // Assert
        success.Should().BeTrue();
        decodedFrame.Type.Should().Be(originalFrame.Type);
        decodedFrame.Flags.Should().Be(originalFrame.Flags);
        decodedFrame.CorrelationId.Should().Be(originalFrame.CorrelationId);
        decodedFrame.Payload.Should().Equal(originalFrame.Payload);
    }
}