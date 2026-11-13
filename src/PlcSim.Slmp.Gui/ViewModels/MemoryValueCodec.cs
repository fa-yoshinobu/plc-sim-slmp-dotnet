using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PlcSim.Slmp.Core.Control;
using PlcSim.Slmp.Core.Engine;
using PlcSim.Slmp.Core.Model;
using PlcSim.Slmp.Core.Protocol;

namespace PlcSim.Slmp.Gui.ViewModels;

public static class MemoryValueCodec
{
    public static string FormatWord(ushort[] words, int i, string format)
    {
        var v = words[i];
        switch (format)
        {
            case "hex":
                return v.ToString("X4");
            case "s16":
                return ((short)v).ToString();
            case "asc":
                return $"{AsciiChar((byte)v)}{AsciiChar((byte)(v >> 8))}";
            case "u32":
            case "f32":
                if (i % 2 == 1 || i + 1 >= words.Length)
                    return string.Empty;
                var d = (uint)(v | (words[i + 1] << 16));
                return format == "u32" ? d.ToString() : BitConverter.Int32BitsToSingle((int)d).ToString("R", CultureInfo.InvariantCulture);
            default:
                return v.ToString();
        }

        static char AsciiChar(byte b) => b is >= 32 and < 127 ? (char)b : '·';
    }

    public static string LabelValueText(LabelValueView label)
    {
        var d = label.Data;
        return label.Type.ToUpperInvariant() switch
        {
            "BIT" or "BOOL" => d[0] != 0 ? "ON" : "OFF",
            "WORD" => BinaryPrimitives.ReadUInt16LittleEndian(d).ToString(),
            "INT" => BinaryPrimitives.ReadInt16LittleEndian(d).ToString(),
            "DWORD" => BinaryPrimitives.ReadUInt32LittleEndian(d).ToString(),
            "DINT" => BinaryPrimitives.ReadInt32LittleEndian(d).ToString(),
            "REAL" or "FLOAT" => BinaryPrimitives.ReadSingleLittleEndian(d).ToString("R", CultureInfo.InvariantCulture),
            "LREAL" or "DOUBLE" => BinaryPrimitives.ReadDoubleLittleEndian(d).ToString("R", CultureInfo.InvariantCulture),
            _ => (label.Type.StartsWith("WSTRING(", StringComparison.OrdinalIgnoreCase) ? Encoding.Unicode : Encoding.ASCII).GetString(d).TrimEnd('\0'),
        };
    }

    public static ushort ParseWord(string text, bool signed = false)
        => unchecked((ushort)ParseInteger(text, signed ? short.MinValue : 0, signed ? short.MaxValue : ushort.MaxValue));

    public static ushort ParseAsciiWord(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ParseWord(text);
        var bytes = Encoding.ASCII.GetBytes(text.PadRight(2, '\0')[..2]);
        return (ushort)(bytes[0] | (bytes[1] << 8));
    }

    public static uint ParseDWord(string text, bool signed = false)
        => unchecked((uint)ParseInteger(text, signed ? int.MinValue : 0, signed ? int.MaxValue : uint.MaxValue));

    public static long ParseInteger(string text, long minimum, long maximum)
    {
        text = text.Trim().Replace(",", string.Empty);
        var hex = true;
        long value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else if (text.EndsWith('h') || text.EndsWith('H'))
            value = long.Parse(text[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        else
        {
            hex = false;
            value = long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }
        if (hex && minimum < 0)
        {
            minimum = 0;
            maximum = maximum * 2 + 1;
        }
        if (value < minimum || value > maximum)
            throw new OverflowException($"値は {minimum}〜{maximum} の範囲で入力してください。");
        return value;
    }
}
