namespace PSX.Services;

internal static class BridgePayloadGuard
{
    public static bool ExceedsBase64DecodedLimit(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var encodedCharacters = 0;
        var paddingCharacters = 0;
        for (var index = value.Length - 1; index >= 0; index--)
        {
            var character = value[index];
            if (char.IsWhiteSpace(character))
                continue;
            if (character == '=' && paddingCharacters < 2 && encodedCharacters == paddingCharacters)
                paddingCharacters++;
            encodedCharacters++;
        }

        if (encodedCharacters == 0 || encodedCharacters % 4 != 0)
            return false;

        var decodedUpperBound = ((long)encodedCharacters / 4 * 3) - paddingCharacters;
        return decodedUpperBound > maxBytes;
    }
}
