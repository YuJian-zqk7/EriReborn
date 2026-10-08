using System.Text;

namespace EriReborn.Cloud;

/// <summary>
/// 123 云盘's signed query string. The live share/download endpoints reject an unsigned request, and
/// failing over to guessing is hopeless, so this is a direct port of the published encode123 scheme:
/// the minute-of-request is mapped through a fixed alphabet into two CRC32 values.
/// </summary>
public static class Pan123Signature
{
    private const string CharMap = "adefghlmyijnopkqrstubcvwsz";

    /// <summary>Builds the "?y=time-a-crc" query the 123 endpoints expect.</summary>
    public static string Encode(string path, string way, string version, long timestampMs)
    {
        var randomInt = Random.Shared.Next(1, 10_000_001);
        var a = 1000L * randomInt;

        var seconds = timestampMs / 1000;
        var timeStr = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("yyyyMMddHHmm");

        var g = new StringBuilder();
        foreach (var c in timeStr)
        {
            var digit = c - '0';
            g.Append(digit == 0 ? CharMap[0] : CharMap[digit - 1]);
        }

        var y = Crc32(g.ToString());
        var finalCrc = Crc32(seconds + "|" + a + "|" + path + "|" + way + "|" + version + "|" + y);
        return "?" + y + "=" + seconds + "-" + a + "-" + finalCrc;
    }

    private static uint Crc32(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
