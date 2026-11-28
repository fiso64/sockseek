using Sockseek.Core.Models;
using System.Text;
using SmallestCSV;

namespace Sockseek.Core.Extractors;

// Keep original record boundaries, including mixed line endings and quoted CSV newlines.
internal sealed class SourceRecordReader : StreamReader
{
    private readonly SmallestCSVParser parser;
    private readonly StringBuilder raw = new();

    public SourceRecordReader(Stream source) : base(source, new UTF8Encoding(false, true))
        => parser = new SmallestCSVParser(this);

    public override int Read()
    {
        int value = base.Read();
        if (value >= 0) raw.Append((char)value);
        return value;
    }

    public List<string>? ReadRecord(bool csv = true)
    {
        raw.Clear();
        if (csv) return parser.ReadNextRow();
        if (Peek() < 0) return null;
        int character;
        while ((character = Read()) >= 0 && character is not ('\r' or '\n')) { }
        if (character == '\r' && Peek() == '\n') Read();
        return [RawRecord.TrimEnd('\r', '\n')];
    }

    public string RawRecord => raw.ToString();

    public string Fingerprint(string header = "")
        => SourceMutation.Fingerprint(header + RawRecord.TrimEnd('\r', '\n'));
}
