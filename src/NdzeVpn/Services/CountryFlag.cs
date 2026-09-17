using System.Text;

namespace NdzeVpn.Services;

public static class CountryFlag
{
    /// <summary>
    /// "🇩🇪 Germany #1" → ("DE", "Germany #1"). Windows cannot render flag emoji, so the UI shows
    /// the two-letter code on the cover art instead. Returns ("", name) when there is no flag.
    /// </summary>
    public static (string Code, string Name) Split(string name)
    {
        var code = new StringBuilder();
        var consumed = 0;

        foreach (var rune in name.EnumerateRunes())
        {
            if (rune.Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                code.Append((char)('A' + rune.Value - 0x1F1E6));
                consumed += rune.Utf16SequenceLength;
                if (code.Length == 2) break;
            }
            else if (code.Length == 0 && Rune.IsWhiteSpace(rune))
            {
                consumed += rune.Utf16SequenceLength;
            }
            else break;
        }

        return code.Length == 2
            ? (code.ToString(), name[consumed..].Trim(' ', '|', '-', '·'))
            : ("", name);
    }
}
