using System.Text;

namespace TutsVideoPlayer.Core.Catalog;

public static class NaturalSortKey
{
    private const int DigitRunWidth = 18;
    private const char TextChunkTerminator = '\u0001';
    private const char DigitChunkTerminator = '\u0002';

    public static string Generate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var builder = new StringBuilder(name.Length + 16);
        var index = 0;

        while (index < name.Length)
        {
            if (char.IsAsciiDigit(name[index]))
            {
                var start = index;
                while (index < name.Length && char.IsAsciiDigit(name[index]))
                {
                    index++;
                }

                var digits = name[start..index].TrimStart('0');
                if (digits.Length == 0)
                {
                    digits = "0";
                }

                if (digits.Length > DigitRunWidth)
                {
                    digits = digits[^DigitRunWidth..];
                }

                builder.Append(digits.PadLeft(DigitRunWidth, '0'));
                builder.Append(DigitChunkTerminator);
            }
            else
            {
                var start = index;
                while (index < name.Length && !char.IsAsciiDigit(name[index]))
                {
                    index++;
                }

                builder.Append(name[start..index]);
                builder.Append(TextChunkTerminator);
            }
        }

        return builder.ToString();
    }
}
