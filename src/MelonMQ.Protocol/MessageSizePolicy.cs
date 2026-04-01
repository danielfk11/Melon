namespace MelonMQ.Protocol;

public static class MessageSizePolicy
{
    private const int MinimumMessageSizeBytes = 1024;
    private const int DefaultEnvelopeOverheadBytes = 16 * 1024;

    public static int ComputeMaxFrameSizeBytes(int maxMessageSizeBytes, int envelopeOverheadBytes = DefaultEnvelopeOverheadBytes)
    {
        if (maxMessageSizeBytes < MinimumMessageSizeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageSizeBytes),
                $"Maximum message size must be at least {MinimumMessageSizeBytes} bytes.");
        }

        if (envelopeOverheadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(envelopeOverheadBytes),
                "Envelope overhead cannot be negative.");
        }

        var base64Size = checked(((maxMessageSizeBytes + 2) / 3) * 4);
        return checked(base64Size + envelopeOverheadBytes);
    }
}