using System.Text;

namespace LinML.Runtime.Onnx;

/// <summary>
/// Lightweight, zero-dependency streaming Protocol Buffers reader for ONNX ModelProto.
/// Safely skips large tensor initializers and weights to extract graph topology and metadata in milliseconds.
/// </summary>
internal static class OnnxProtobufReader
{
    public sealed record ParsedProto
    {
        public long IrVersion { get; init; }
        public List<(string Domain, long Version)> OpsetImports { get; init; } = [];
        public string? ProducerName { get; init; }
        public string? ProducerVersion { get; init; }
        public string? Domain { get; init; }
        public long ModelVersion { get; init; }
        public string? DocString { get; init; }
        public Dictionary<string, string> MetadataProps { get; init; } = [];
        public int NodeCount { get; init; }
        public List<string> Operators { get; init; } = [];
    }

    public static ParsedProto Parse(Stream stream)
    {
        long irVersion = 0;
        var opsets = new List<(string Domain, long Version)>();
        string? producerName = null;
        string? producerVersion = null;
        string? domain = null;
        long modelVersion = 0;
        string? docString = null;
        var metadata = new Dictionary<string, string>();
        var ops = new HashSet<string>(StringComparer.Ordinal);
        int nodeCount = 0;

        while (stream.Position < stream.Length)
        {
            if (!TryReadTag(stream, out int fieldNumber, out int wireType)) break;

            if (wireType == 0) // Varint
            {
                var val = ReadVarint(stream);
                if (fieldNumber == 1) irVersion = (long)val;
                else if (fieldNumber == 5) modelVersion = (long)val;
            }
            else if (wireType == 2) // Length-delimited
            {
                long len = (long)ReadVarint(stream);
                long end = stream.Position + len;

                if (fieldNumber == 2) // producer_name
                {
                    producerName = ReadString(stream, (int)len);
                }
                else if (fieldNumber == 3) // producer_version
                {
                    producerVersion = ReadString(stream, (int)len);
                }
                else if (fieldNumber == 4) // domain
                {
                    domain = ReadString(stream, (int)len);
                }
                else if (fieldNumber == 6) // doc_string
                {
                    docString = ReadString(stream, (int)len);
                }
                else if (fieldNumber == 8) // opset_import
                {
                    var op = ParseOpset(stream, end);
                    opsets.Add(op);
                }
                else if (fieldNumber == 14) // metadata_props
                {
                    var (k, v) = ParseStringEntry(stream, end);
                    if (k != null) metadata[k] = v ?? "";
                }
                else if (fieldNumber == 7) // graph
                {
                    nodeCount += ParseGraph(stream, end, ops);
                }
                else
                {
                    stream.Seek(len, SeekOrigin.Current);
                }

                if (stream.Position != end)
                {
                    stream.Position = Math.Min(end, stream.Length);
                }
            }
            else if (wireType == 1) stream.Seek(8, SeekOrigin.Current);
            else if (wireType == 5) stream.Seek(4, SeekOrigin.Current);
            else break;
        }

        var sortedOps = ops.OrderBy(o => o, StringComparer.Ordinal).ToList();
        return new ParsedProto
        {
            IrVersion = irVersion,
            OpsetImports = opsets,
            ProducerName = producerName,
            ProducerVersion = producerVersion,
            Domain = domain,
            ModelVersion = modelVersion,
            DocString = docString,
            MetadataProps = metadata,
            NodeCount = nodeCount,
            Operators = sortedOps
        };
    }

    private static (string Domain, long Version) ParseOpset(Stream stream, long end)
    {
        string domain = "";
        long version = 0;
        while (stream.Position < end)
        {
            if (!TryReadTag(stream, out int fieldNumber, out int wireType)) break;
            if (wireType == 0)
            {
                long val = (long)ReadVarint(stream);
                if (fieldNumber == 2) version = val;
            }
            else if (wireType == 2)
            {
                long len = (long)ReadVarint(stream);
                if (fieldNumber == 1) domain = ReadString(stream, (int)len);
                else stream.Seek(len, SeekOrigin.Current);
            }
            else SkipField(stream, wireType);
        }
        return (domain, version);
    }

    private static (string? Key, string? Value) ParseStringEntry(Stream stream, long end)
    {
        string? key = null;
        string? val = null;
        while (stream.Position < end)
        {
            if (!TryReadTag(stream, out int fieldNumber, out int wireType)) break;
            if (wireType == 2)
            {
                long len = (long)ReadVarint(stream);
                if (fieldNumber == 1) key = ReadString(stream, (int)len);
                else if (fieldNumber == 2) val = ReadString(stream, (int)len);
                else stream.Seek(len, SeekOrigin.Current);
            }
            else SkipField(stream, wireType);
        }
        return (key, val);
    }

    private static int ParseGraph(Stream stream, long end, HashSet<string> ops)
    {
        int count = 0;
        while (stream.Position < end)
        {
            if (!TryReadTag(stream, out int fieldNumber, out int wireType)) break;
            if (wireType == 2)
            {
                long len = (long)ReadVarint(stream);
                long nodeEnd = stream.Position + len;
                if (fieldNumber == 1) // node (NodeProto)
                {
                    count++;
                    ParseNode(stream, nodeEnd, ops);
                }
                else
                {
                    stream.Seek(len, SeekOrigin.Current);
                }

                if (stream.Position != nodeEnd)
                {
                    stream.Position = Math.Min(nodeEnd, stream.Length);
                }
            }
            else SkipField(stream, wireType);
        }
        return count;
    }

    private static void ParseNode(Stream stream, long end, HashSet<string> ops)
    {
        while (stream.Position < end)
        {
            if (!TryReadTag(stream, out int fieldNumber, out int wireType)) break;
            if (wireType == 2)
            {
                long len = (long)ReadVarint(stream);
                if (fieldNumber == 4) // op_type
                {
                    string op = ReadString(stream, (int)len);
                    if (!string.IsNullOrEmpty(op)) ops.Add(op);
                }
                else
                {
                    stream.Seek(len, SeekOrigin.Current);
                }
            }
            else SkipField(stream, wireType);
        }
    }

    private static bool TryReadTag(Stream stream, out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (stream.Position >= stream.Length) return false;
        ulong tag = ReadVarint(stream);
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 7);
        return fieldNumber > 0;
    }

    private static ulong ReadVarint(Stream stream)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) break;
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static string ReadString(Stream stream, int length)
    {
        byte[] bytes = new byte[length];
        int read = 0;
        while (read < length)
        {
            int r = stream.Read(bytes, read, length - read);
            if (r <= 0) break;
            read += r;
        }
        return Encoding.UTF8.GetString(bytes, 0, read);
    }

    private static void SkipField(Stream stream, int wireType)
    {
        if (wireType == 0) ReadVarint(stream);
        else if (wireType == 1) stream.Seek(8, SeekOrigin.Current);
        else if (wireType == 2)
        {
            long len = (long)ReadVarint(stream);
            stream.Seek(len, SeekOrigin.Current);
        }
        else if (wireType == 5) stream.Seek(4, SeekOrigin.Current);
    }
}
