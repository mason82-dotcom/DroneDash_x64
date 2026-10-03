using System.IO;
using System.Text;
using System.Xml.Linq;

namespace DroneDash_x64.Desktop.Imaging;

public sealed record DjiXmpMetadataEntry(
    string NamespaceName,
    string LocalName,
    string QualifiedName,
    string Value);

public sealed record DjiXmpMetadata(
    bool Found,
    IReadOnlyList<DjiXmpMetadataEntry> Entries,
    IReadOnlyDictionary<string, string> Fields,
    string? Error);

public static class DjiXmpMetadataReader
{
    private const int MaxScanBytes = 32 * 1024 * 1024;
    private const int ReadBufferBytes = 64 * 1024;
    private const int MarkerCarryChars = 32;
    private const int MaxPacketChars = MaxScanBytes;

    private static readonly Encoding Utf8 =
        new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: false);

    public static DjiXmpMetadata Read(string path)
    {
        var packet = ReadPacket(path);

        if (!packet.Found)
            return Empty(found: false, error: null);

        if (packet.Content is null)
            return Empty(found: true, error: packet.Error);

        XDocument document;
        try
        {
            document = XDocument.Parse(
                packet.Content,
                LoadOptions.None);
        }
        catch (Exception ex)
        {
            return Empty(
                found: true,
                error:
                    "XMP vorhanden, aber XML konnte nicht geparst werden " +
                    $"({ex.GetType().Name}).");
        }

        var namespacePrefixes =
            (document.Root?.DescendantsAndSelf() ??
             Enumerable.Empty<XElement>())
            .SelectMany(element =>
                element.Attributes()
                    .Where(attribute =>
                        attribute.IsNamespaceDeclaration))
            .GroupBy(
                attribute => attribute.Value,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                    group.First().Name.LocalName == "xmlns"
                        ? ""
                        : group.First().Name.LocalName,
                StringComparer.Ordinal);

        var entries =
            new List<DjiXmpMetadataEntry>();
        var fields =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
        var seen =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in
                     element.Attributes()
                         .Where(attribute =>
                             !attribute.IsNamespaceDeclaration))
            {
                AddEntry(
                    entries,
                    fields,
                    seen,
                    attribute.Name,
                    attribute.Value,
                    namespacePrefixes);
            }

            if (!element.HasElements)
            {
                AddEntry(
                    entries,
                    fields,
                    seen,
                    element.Name,
                    element.Value,
                    namespacePrefixes);
            }
        }

        return new DjiXmpMetadata(
            true,
            entries,
            fields,
            null);
    }

    private static void AddEntry(
        ICollection<DjiXmpMetadataEntry> entries,
        IDictionary<string, string> fields,
        ISet<string> seen,
        XName name,
        string rawValue,
        IReadOnlyDictionary<string, string> namespacePrefixes)
    {
        var value = rawValue.Trim();
        if (value.Length == 0)
            return;

        var qualifiedName =
            QualifiedName(
                name,
                namespacePrefixes);

        if (!seen.Add(
                qualifiedName +
                "\0" +
                value))
        {
            return;
        }

        entries.Add(
            new DjiXmpMetadataEntry(
                name.NamespaceName,
                name.LocalName,
                qualifiedName,
                value));

        fields.TryAdd(
            name.LocalName,
            value);
    }

    private static PacketReadResult ReadPacket(
        string path)
    {
        using var stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ReadBufferBytes,
                FileOptions.SequentialScan);

        var decoder =
            Utf8.GetDecoder();
        var bytes =
            new byte[ReadBufferBytes];
        var chars =
            new char[
                Utf8.GetMaxCharCount(
                    ReadBufferBytes)];

        var remaining =
            Math.Min(
                stream.Length,
                MaxScanBytes);

        var carry =
            string.Empty;
        StringBuilder? packet =
            null;
        string? endTag =
            null;

        while (remaining > 0)
        {
            var requested =
                (int)Math.Min(
                    bytes.Length,
                    remaining);

            var read =
                stream.Read(
                    bytes,
                    0,
                    requested);

            if (read == 0)
                break;

            remaining -=
                read;

            var charCount =
                decoder.GetChars(
                    bytes,
                    0,
                    read,
                    chars,
                    0,
                    flush: false);

            if (charCount == 0)
                continue;

            var chunk =
                new string(
                    chars,
                    0,
                    charCount);

            if (packet is null)
            {
                var combined =
                    carry.Length == 0
                        ? chunk
                        : carry + chunk;

                if (!TryFindStart(
                        combined,
                        out var start,
                        out endTag))
                {
                    carry =
                        combined.Length >
                        MarkerCarryChars
                            ? combined[
                                ^MarkerCarryChars..]
                            : combined;

                    continue;
                }

                packet =
                    new StringBuilder(
                        Math.Min(
                            combined.Length -
                            start +
                            4096,
                            MaxPacketChars));

                packet.Append(
                    combined,
                    start,
                    combined.Length -
                    start);

                if (TryComplete(
                        packet,
                        endTag!,
                        previousLength: 0,
                        out var completed))
                {
                    return new PacketReadResult(
                        true,
                        completed,
                        null);
                }

                if (packet.Length >
                    MaxPacketChars)
                {
                    return new PacketReadResult(
                        true,
                        null,
                        "XMP-Paket überschreitet das zulässige Scan-Limit.");
                }

                continue;
            }

            var previousLength =
                packet.Length;

            packet.Append(
                chunk);

            if (TryComplete(
                    packet,
                    endTag!,
                    previousLength,
                    out var content))
            {
                return new PacketReadResult(
                    true,
                    content,
                    null);
            }

            if (packet.Length >
                MaxPacketChars)
            {
                return new PacketReadResult(
                    true,
                    null,
                    "XMP-Paket überschreitet das zulässige Scan-Limit.");
            }
        }

        return packet is null
            ? new PacketReadResult(
                false,
                null,
                null)
            : new PacketReadResult(
                true,
                null,
                "XMP vorhanden, aber innerhalb des Scan-Limits unvollständig.");
    }

    private static bool TryFindStart(
        string text,
        out int start,
        out string? endTag)
    {
        var prefixed =
            text.IndexOf(
                "<x:xmpmeta",
                StringComparison.OrdinalIgnoreCase);

        var plain =
            text.IndexOf(
                "<xmpmeta",
                StringComparison.OrdinalIgnoreCase);

        if (prefixed < 0 &&
            plain < 0)
        {
            start = -1;
            endTag = null;
            return false;
        }

        if (plain < 0 ||
            (prefixed >= 0 &&
             prefixed <= plain))
        {
            start = prefixed;
            endTag = "</x:xmpmeta>";
            return true;
        }

        start = plain;
        endTag = "</xmpmeta>";
        return true;
    }

    private static bool TryComplete(
        StringBuilder packet,
        string endTag,
        int previousLength,
        out string? content)
    {
        var searchStart =
            Math.Max(
                0,
                previousLength -
                endTag.Length);

        var tail =
            packet.ToString(
                searchStart,
                packet.Length -
                searchStart);

        var relativeEnd =
            tail.IndexOf(
                endTag,
                StringComparison.OrdinalIgnoreCase);

        if (relativeEnd < 0)
        {
            content = null;
            return false;
        }

        var absoluteEnd =
            searchStart +
            relativeEnd +
            endTag.Length;

        content =
            packet.ToString(
                0,
                absoluteEnd);

        return true;
    }

    private static string QualifiedName(
        XName name,
        IReadOnlyDictionary<string, string> namespacePrefixes)
    {
        if (name.NamespaceName.Length == 0)
            return name.LocalName;

        return
            namespacePrefixes.TryGetValue(
                name.NamespaceName,
                out var prefix) &&
            !string.IsNullOrWhiteSpace(
                prefix)
                ? $"{prefix}:{name.LocalName}"
                : name.LocalName;
    }

    private static DjiXmpMetadata Empty(
        bool found,
        string? error) =>
        new(
            found,
            Array.Empty<DjiXmpMetadataEntry>(),
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase),
            error);

    private sealed record PacketReadResult(
        bool Found,
        string? Content,
        string? Error);
}
