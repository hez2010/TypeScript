namespace TypeScript.Compiler.Configuration;

// Validate the reference's language tags without loading OS culture data.
internal static partial class LocaleIdentifier
{
    internal static bool IsValid(Utf8String value)
    {
        Utf8String tag = value.Replace((byte)'_', (byte)'-').ToLowerInvariant();
        if (Grandfathered.Contains(tag))
            return true;
        Utf8String[] parts = tag.Split((byte)'-');
        foreach (Utf8String part in parts)
            if (part.Length is < 1 or > 8 || !AlphaNumeric(part))
                return false;
        int index = 0;
        if (parts[0] != Utf8Literals.X && !ParseTag(parts, ref index))
            return false;
        if (index < parts.Length && parts[index].Length != 1)
            return false;
        while (index < parts.Length)
        {
            if (parts[index].Length != 1)
                return true; // The reference stops after the last complete extension.
            Utf8String extension = parts[index++];
            int start = index;
            if (extension == Utf8Literals.X)
                return index < parts.Length;
            if (extension == Utf8Literals.LowerT)
            {
                if (index < parts.Length && parts[index].Length is 2 or 3 && Utf8Ascii.IsLetter(parts[index][1])
                    && !ParseTag(parts, ref index))
                    return false;
                while (index < parts.Length && parts[index].Length == 2 && Utf8Ascii.IsDigit(parts[index][1]))
                {
                    index++;
                    while (index < parts.Length && parts[index].Length >= 3)
                        index++;
                }
            }
            else if (extension == Utf8Literals.U)
            {
                while (index < parts.Length && parts[index].Length >= 3)
                    index++;
                Dictionary<Utf8String, Utf8String> keys = [];
                while (index < parts.Length && parts[index].Length == 2)
                {
                    Utf8String key = parts[index++];
                    int field = index;
                    while (index < parts.Length && parts[index].Length >= 3)
                        index++;
                    Utf8String type = Utf8String.Join((byte)'-', parts, field, index - field);
                    if (keys.TryGetValue(key, out Utf8String previous) && previous != type)
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

    private static bool ParseTag(Utf8String[] parts, ref int index)
    {
        if (!Languages.Contains(parts[index++]))
            return false;
        while (index < parts.Length && parts[index].Length == 3 && Utf8Ascii.IsLetter(parts[index][0]))
            if (!Languages.Contains(parts[index++]))
                return false;
        if (index < parts.Length && parts[index].Length == 4 && Utf8Ascii.IsLetter(parts[index][0])
            && !Scripts.Contains(parts[index++]))
            return false;
        if (index < parts.Length && parts[index].Length is 2 or 3 && !Regions.Contains(parts[index++]))
            return false;
        while (index < parts.Length && parts[index].Length >= 4)
            if (!Variants.Contains(parts[index++]))
                return false;
        return true;
    }

    private static bool AlphaNumeric(Utf8String value)
    {
        foreach (byte b in value)
            if (!Utf8Ascii.IsLetterOrDigit(b))
                return false;
        return true;
    }
}
