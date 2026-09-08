using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UnifiedCalendar.Core.Models;

public readonly record struct EventKey
{
    public EventKey(
        ProviderKind provider,
        Guid internalAccountId,
        string calendarId,
        string sourceEventId,
        string? occurrenceKey = null)
    {
        ModelGuard.Defined(provider, nameof(provider));
        ModelGuard.NotEmpty(internalAccountId, nameof(internalAccountId));
        ModelGuard.NotBlank(calendarId, nameof(calendarId));
        ModelGuard.NotBlank(sourceEventId, nameof(sourceEventId));

        Provider = provider;
        InternalAccountId = internalAccountId;
        CalendarId = calendarId;
        SourceEventId = sourceEventId;
        OccurrenceKey = occurrenceKey ?? string.Empty;
    }

    public ProviderKind Provider { get; }

    public Guid InternalAccountId { get; }

    public string CalendarId { get; }

    public string SourceEventId { get; }

    public string OccurrenceKey { get; }

    public bool IsValid => Enum.IsDefined(Provider)
        && InternalAccountId != Guid.Empty
        && !string.IsNullOrWhiteSpace(CalendarId)
        && !string.IsNullOrWhiteSpace(SourceEventId)
        && OccurrenceKey is not null;

    public string ToStableId()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException("A default or otherwise invalid event key has no stable ID.");
        }

        using var stream = new MemoryStream();
        WriteComponent(stream, Provider.ToString().ToLowerInvariant());
        WriteComponent(stream, InternalAccountId.ToString("N"));
        WriteComponent(stream, CalendarId);
        WriteComponent(stream, SourceEventId);
        WriteComponent(stream, OccurrenceKey);

        var hash = SHA256.HashData(stream.ToArray());
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void WriteComponent(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
