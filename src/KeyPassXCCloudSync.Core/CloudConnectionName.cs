namespace KeyPassXCCloudSync;

/// <summary>
/// Validates a human-readable cloud label, not a remote key or CloudId.
/// The label is also used as the suffix of an INI [Cloud.NAME] section.
/// Keep INI delimiters and control characters out of it.
/// </summary>
internal static class CloudConnectionName
{
    internal const int MaxLength = 80;

    internal static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxLength ||
            !string.Equals(name, name.Trim(), StringComparison.Ordinal))
            return false;

        var hasLetterOrDigit = false;
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                hasLetterOrDigit = true;
                continue;
            }
            // Deliberately allow only ordinary spaces, not tabs or newlines.
            // These punctuation marks are safe inside [Cloud.<label>] and JSON.
            if (c is ' ' or '_' or '-' or '.' or '(' or ')' or '&' or '+')
                continue;
            return false;
        }
        return hasLetterOrDigit;
    }
}
