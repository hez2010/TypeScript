namespace TypeScript.Compiler.Configuration;

// Validate the reference's language tags without loading OS culture data.
internal static partial class LocaleIdentifier
{
    internal static bool IsValid(string value)
    {
        string tag = value.Replace('_', '-').ToLowerInvariant();
        if (Grandfathered.Contains(tag))
            return true;
        string[] parts = tag.Split('-');
        foreach (string part in parts)
            if (part.Length is < 1 or > 8 || part.Any(c => !char.IsAsciiLetterOrDigit(c)))
                return false;
        int index = 0;
        if (parts[0] != "x" && !ParseTag(parts, ref index))
            return false;
        if (index < parts.Length && parts[index].Length != 1)
            return false;
        while (index < parts.Length)
        {
            if (parts[index].Length != 1)
                return true; // The reference stops after the last complete extension.
            string extension = parts[index++];
            int start = index;
            if (extension == "x")
                return index < parts.Length;
            if (extension == "t")
            {
                if (index < parts.Length && parts[index].Length is 2 or 3 && char.IsAsciiLetter(parts[index][1])
                    && !ParseTag(parts, ref index))
                    return false;
                while (index < parts.Length && parts[index].Length == 2 && char.IsAsciiDigit(parts[index][1]))
                {
                    index++;
                    while (index < parts.Length && parts[index].Length >= 3)
                        index++;
                }
            }
            else if (extension == "u")
            {
                while (index < parts.Length && parts[index].Length >= 3)
                    index++;
                Dictionary<string, string> keys = [];
                while (index < parts.Length && parts[index].Length == 2)
                {
                    string key = parts[index++];
                    int field = index;
                    while (index < parts.Length && parts[index].Length >= 3)
                        index++;
                    string type = string.Join('-', parts, field, index - field);
                    if (keys.TryGetValue(key, out string? previous) && previous != type)
                        return false;
                    keys[key] = type;
                }
            }
            else
                while (index < parts.Length && parts[index].Length >= 2)
                    index++;
            if (index == start)
                return false;
        }
        return true;
    }

    private static bool ParseTag(string[] parts, ref int index)
    {
        if (!Languages.Contains(parts[index++]))
            return false;
        while (index < parts.Length && parts[index].Length == 3 && char.IsAsciiLetter(parts[index][0]))
            if (!Languages.Contains(parts[index++]))
                return false;
        if (index < parts.Length && parts[index].Length == 4 && char.IsAsciiLetter(parts[index][0])
            && !Scripts.Contains(parts[index++]))
            return false;
        if (index < parts.Length && parts[index].Length is 2 or 3 && !Regions.Contains(parts[index++]))
            return false;
        while (index < parts.Length && parts[index].Length >= 4)
            if (!Variants.Contains(parts[index++]))
                return false;
        return true;
    }
}
