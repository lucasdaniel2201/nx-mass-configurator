namespace NxMassConfigurator.Services.Qnap;

/// <summary>
/// Minimal JSON parser for QNAP FileStation API responses.
/// Avoids heavy System.Text.Json allocations per call.
/// </summary>
internal static class QnapJsonParser
{
    /// <summary>
    /// Parses a FileStation get_list JSON response into a list of entries.
    /// Expected format: {"datas":[{"filename":"...","isfolder":1},...]}
    /// </summary>
    public static List<QnapFileEntry> ParseFileList(string json)
    {
        var results = new List<QnapFileEntry>();

        // Find the "datas" array
        var datasIdx = json.IndexOf("\"datas\"", StringComparison.Ordinal);
        if (datasIdx < 0) return results;

        var bracketIdx = json.IndexOf('[', datasIdx);
        if (bracketIdx < 0) return results;

        // Find matching closing bracket
        var endIdx = FindMatchingBracket(json, bracketIdx);
        if (endIdx < 0) return results;

        var datasContent = json.Substring(bracketIdx + 1, endIdx - bracketIdx - 1);

        // Split by objects
        var objects = SplitJsonObjects(datasContent);
        foreach (var obj in objects)
        {
            var filename = ExtractStringValue(obj, "filename");
            var isFolder = ExtractIntValue(obj, "isfolder") == 1;

            if (!string.IsNullOrWhiteSpace(filename))
                results.Add(new QnapFileEntry { FileName = filename, IsFolder = isFolder });
        }

        return results;
    }

    /// <summary>
    /// Parses the delete response to extract "success" and "status" fields.
    /// </summary>
    public static (string? success, int? status) ParseDeleteResult(string json)
    {
        var success = ExtractStringValue(json, "success");
        var status = ExtractIntValue(json, "status");
        return (success, status);
    }

    private static string? ExtractStringValue(string json, string key)
    {
        var keyPattern = $"\"{key}\"";
        var keyIdx = json.IndexOf(keyPattern, StringComparison.Ordinal);
        if (keyIdx < 0) return null;

        var colonIdx = json.IndexOf(':', keyIdx + keyPattern.Length);
        if (colonIdx < 0) return null;

        var valueIdx = SkipWhitespace(json, colonIdx + 1);
        if (valueIdx < 0) return null;

        if (valueIdx < json.Length && json[valueIdx] == '"')
        {
            // String value
            var endQuote = json.IndexOf('"', valueIdx + 1);
            if (endQuote < 0) return json.Substring(valueIdx + 1);
            return json.Substring(valueIdx + 1, endQuote - valueIdx - 1);
        }

        // Unquoted value (number/bool)
        var end = json.IndexOfAny(new[] { ',', '}', '\n', '\r' }, valueIdx);
        if (end < 0) end = json.Length;
        return json.Substring(valueIdx, end - valueIdx).Trim();
    }

    private static int? ExtractIntValue(string json, string key)
    {
        var strVal = ExtractStringValue(json, key);
        if (strVal == null) return null;
        return int.TryParse(strVal, out var val) ? val : null;
    }

    private static List<string> SplitJsonObjects(string content)
    {
        var objects = new List<string>();
        var depth = 0;
        var start = -1;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    objects.Add(content.Substring(start, i - start + 1));
                    start = -1;
                }
            }
            else if (c == '"')
            {
                // Skip strings
                i = SkipString(content, i);
            }
        }

        return objects;
    }

    private static int FindMatchingBracket(string json, int openIdx)
    {
        var depth = 0;
        for (var i = openIdx; i < json.Length; i++)
        {
            var c = json[i];
            if (c == '[') depth++;
            else if (c == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
            else if (c == '"') i = SkipString(json, i);
        }
        return -1;
    }

    private static int SkipString(string json, int startQuote)
    {
        for (var i = startQuote + 1; i < json.Length; i++)
        {
            if (json[i] == '\\')
            {
                i++; // skip escaped char
                continue;
            }
            if (json[i] == '"')
                return i;
        }
        return json.Length - 1;
    }

    private static int SkipWhitespace(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }
        return -1;
    }
}
