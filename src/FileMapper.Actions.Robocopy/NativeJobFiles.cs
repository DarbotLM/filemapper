using System.Text;
using System.Text.RegularExpressions;

namespace FileMapper.Actions.Robocopy;

public static class NativeJobFiles
{
    // Deliberately a strict RCJ data subset: no /JOB nesting, process invocation or native /SAVE side effects.
    public static JobImport Import(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 1024 * 1024 || text.Contains('\0')) throw new ArgumentException("Native job exceeds 1 MiB or contains NUL.");
        string? source = null, destination = null; var files = new List<string>(); var options = new List<OptionValue>();
        string? list = null; var items = new List<string>();
        void Flush() { if (list == "IF") files.AddRange(items); else if (list is not null) options.Add(new(list, Items: items.ToArray())); list = null; items.Clear(); }
        foreach (var original in text.Split('\n'))
        {
            var line = original.Trim(); if (line.Length == 0 || line.StartsWith("::", StringComparison.Ordinal)) continue;
            // Comments outside quotes follow native RCJ convention; reject inline comments rather than misparse paths.
            if (line.Contains(" ::", StringComparison.Ordinal)) throw new ArgumentException("Inline RCJ comments are not accepted; place comments on their own line.");
            if (line.StartsWith('/'))
            {
                Flush(); var separator = line.IndexOf(':'); var name = (separator < 0 ? line[1..] : line[1..separator]).ToUpperInvariant(); var value = separator < 0 ? null : line[(separator + 1)..];
                if (name is "SD" or "DD")
                {
                    if (value is null) throw new ArgumentException("Endpoint job field requires a literal path.");
                    var path = Unquote(value); if (name == "SD") { if (source is not null) throw new ArgumentException("Duplicate /SD."); source = path; } else { if (destination is not null) throw new ArgumentException("Duplicate /DD."); destination = path; }
                    continue;
                }
                if (name is "IF" or "XF" or "XD") { if (value is not null) throw new ArgumentException("RCJ list values must occupy their own lines."); list = name; continue; }
                if (!RobocopyCatalog.Options.TryGetValue(name, out var catalog) || RobocopyCompiler.TemplateOnly.Contains(name) || RobocopyCompiler.ManagedLog.Contains(name)) throw new ArgumentException("Unknown, nested, logging or settings RCJ option: /" + name);
                var type = catalog.GetProperty("parameter").GetProperty("type").GetString();
                if (type == "booleanSwitch") { if (value is not null) throw new ArgumentException("Boolean RCJ option has a value."); options.Add(new(name, true)); }
                else if (type is "flagSet" or "enum" or "localTimeWindow") options.Add(new(name, Text: value));
                else if (type == "ageOrDate" && value is { Length: 8 }) options.Add(new(name, Text: value));
                else if (value is null) options.Add(new(name));
                else if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)) options.Add(new(name, Number: number));
                else throw new ArgumentException("Unsupported typed RCJ value: /" + name);
            }
            else { if (list is null) throw new ArgumentException("RCJ data outside an explicit list is rejected."); var item = Unquote(line); if (!RobocopyCompiler.SafeItem(item, list == "IF")) throw new ArgumentException("Unsafe RCJ list item."); items.Add(item); }
        }
        Flush(); if (source is null || destination is null) throw new ArgumentException("A complete RCJ plan requires /SD and /DD.");
        var draft = new RobocopyDraft(source, destination) { Files = files.Count > 0 ? files.ToArray() : ["*.*"], Options = options.ToArray() };
        var validation = RobocopyCompiler.Validate(draft); if (!validation.Valid) throw new ArgumentException(string.Join(" ", validation.Issues.Select(x => x.Message)));
        return new(validation.Normalized!, Data.Hash(text), ["Parsed as bounded untrusted RCJ data; no /JOB, /SAVE or native job execution occurred.", "Strict supported grammar; no claim that every historical RCJ dialect is accepted."]);
    }
    public static string Export(RobocopyDraft draft)
    {
        var v = RobocopyCompiler.Validate(draft); if (!v.Valid) throw new ArgumentException(string.Join(" ", v.Issues.Select(x => x.Message)));
        draft = v.Normalized!; var b = new StringBuilder(":: FileMapper typed RCJ export; review separately before external use.\r\n");
        b.Append("/SD:\"").Append(draft.Source).Append("\"\r\n/DD:\"").Append(draft.Destination).Append("\"\r\n/IF\r\n");
        foreach (var item in draft.Files) b.Append("  \"").Append(item).Append("\"\r\n");
        foreach (var o in draft.Options)
        {
            b.Append('/').Append(o.Name); if (o.Number is not null) b.Append(':').Append(o.Number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); else if (o.Text is not null) b.Append(':').Append(o.Text);
            b.Append("\r\n"); if (o.Items is not null) foreach (var item in o.Items) b.Append("  \"").Append(item).Append("\"\r\n");
        }
        return b.ToString();
    }
    private static string Unquote(string value)
    {
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2) value = value[1..^1];
        if (value.Contains('"') || value.Any(char.IsControl)) throw new ArgumentException("Malformed quoting/control characters in RCJ data.");
        return value;
    }
}
