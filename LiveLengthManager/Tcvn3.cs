using System.Text;

namespace LiveLengthManager;

// TCVN3 (ABC) <-> Unicode for notes whose style uses VNROMANS.SHX. In AutoCAD, TCVN3 text is
// stored as the Windows-1252 characters of each TCVN3 byte (e.g. "á" is stored as '¸', 0xB8).
internal static class Tcvn3
{
    private const string Unicode =
        "àảãáạ" + "ăằẳẵắặ" + "âầẩẫấậ" + "đ" + "èẻẽéẹ" + "êềểễếệ" + "ìỉĩíị" + "òỏõóọ" + "ôồổỗốộ" + "ơờởỡớợ" +
        "ùủũúụ" + "ưừửữứự" + "ỳỷỹýỵ" + "ĂÂÊÔƠƯĐ";
    private static readonly byte[] Codes =
    {
        0xB5, 0xB6, 0xB7, 0xB8, 0xB9,
        0xA8, 0xBB, 0xBC, 0xBD, 0xBE, 0xC6,
        0xA9, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB,
        0xAE,
        0xCC, 0xCE, 0xCF, 0xD0, 0xD1,
        0xAA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6,
        0xD7, 0xD8, 0xDC, 0xDD, 0xDE,
        0xDF, 0xE1, 0xE2, 0xE3, 0xE4,
        0xAB, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9,
        0xAC, 0xEA, 0xEB, 0xEC, 0xED, 0xEE,
        0xEF, 0xF1, 0xF2, 0xF3, 0xF4,
        0xAD, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9,
        0xFA, 0xFB, 0xFC, 0xFD, 0xFE,
        0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7
    };
    private static readonly Dictionary<char, char> ToTcvn = new();
    private static readonly Dictionary<char, char> FromTcvn = new();

    static Tcvn3()
    {
        if (Unicode.Length != Codes.Length) throw new InvalidOperationException("TCVN3 table mismatch");
        for (int i = 0; i < Codes.Length; i++)
        {
            ToTcvn[Unicode[i]] = (char)Codes[i];
            FromTcvn[(char)Codes[i]] = Unicode[i];
        }
    }

    /// <summary>Unicode to TCVN3. TCVN3 has no capital letters with tone marks, so those become
    /// lowercase; <paramref name="lossy"/> counts them.</summary>
    internal static string FromUnicode(string text, out int lossy)
    {
        lossy = 0;
        var sb = new StringBuilder(text.Length);
        foreach (var c in DecodeEscapes(text).Normalize(NormalizationForm.FormC))
        {
            if (ToTcvn.TryGetValue(c, out var mapped)) sb.Append(mapped);
            else if (char.IsUpper(c) && ToTcvn.TryGetValue(char.ToLowerInvariant(c), out mapped))
            {
                sb.Append(mapped);
                lossy++;
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    internal static string ToUnicode(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(FromTcvn.TryGetValue(c, out var mapped) ? mapped : c);
        return sb.ToString();
    }

    // MText may store characters as \U+XXXX; decode them so they can be mapped.
    private static string DecodeEscapes(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        @"(?<!\\)((?:\\\\)*)\\U\+([0-9A-Fa-f]{4})",
        m => m.Groups[1].Value + (char)Convert.ToInt32(m.Groups[2].Value, 16));

    /// <summary>Removes inline font overrides (\fArial|b0|...; or \Ffile.shx;) so the style font applies.</summary>
    internal static string StripFontOverrides(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var code = text[i + 1];
                if (code is 'f' or 'F')
                {
                    var end = text.IndexOf(';', i + 2);
                    i = end < 0 ? text.Length : end;
                    continue;
                }
                sb.Append(text, i, 2); // keeps \\ and other codes intact
                i++;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }
}
