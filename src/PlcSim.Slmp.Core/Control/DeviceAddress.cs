using System.Globalization;
using System.Text.RegularExpressions;
using PlcSim.Slmp.Core.Model;

namespace PlcSim.Slmp.Core.Control;

/// <summary>
/// "D100", "X1F", "U0\G10", "U3E0\HG5", "J1\W10" 形式のアドレス。
/// 番号はデバイスの表記基数（X/Y/B/W/SB/SW は 16 進、iQ-F の X/Y は 8 進）で解釈する。
/// </summary>
public sealed record DeviceAddress(DeviceDefinition Device, uint Number, string? Qualifier)
{
    private static readonly Regex Pattern = new(@"^(?:(?<q>[UJ][0-9A-F]+)\\)?(?<dev>[A-Z]+)(?<num>[0-9A-F]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static DeviceAddress Parse(string text, CpuProfile profile)
    {
        var m = Pattern.Match(text.Trim().ToUpperInvariant());
        if (!m.Success)
            throw new FormatException($"アドレス '{text}' を解釈できません（例: D100, M0, X1F, U0\\G10, J1\\W10）");

        var letters = m.Groups["dev"].Value;
        var digits = m.Groups["num"].Value;

        // デバイス名と番号の境界を最長一致で決める（例: "B1F" は B + 1F、"SB10" は SB + 10）。
        for (var length = letters.Length; length >= 1; length--)
        {
            var name = letters[..length];
            var def = DeviceCatalog.FindByName(name);
            if (def is null)
                continue;
            var numberText = letters[length..] + digits;
            if (TryParseNumber(numberText, (int)profile.RadixOf(def), out var number))
                return new DeviceAddress(def, number, m.Groups["q"].Success ? m.Groups["q"].Value : null);
        }

        throw new FormatException($"アドレス '{text}' のデバイス名または番号が不正です");
    }

    public override string ToString() => Qualifier is null ? $"{Device.Name}{Number}" : $"{Qualifier}\\{Device.Name}{Number}";

    private static bool TryParseNumber(string text, int radix, out uint number)
    {
        number = 0;
        if (text.Length == 0)
            return false;
        try
        {
            number = radix switch
            {
                16 => uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                8 => Convert.ToUInt32(text, 8),
                _ => uint.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture),
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return false;
        }
    }
}
