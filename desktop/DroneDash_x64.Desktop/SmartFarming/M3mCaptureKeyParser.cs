using System.IO;
using System.Text.RegularExpressions;

namespace DroneDash_x64.Desktop.SmartFarming;

public static partial class M3mCaptureKeyParser
{
    [GeneratedRegex(
        @"^(?<key>DJI_\d{14}_\d{4})_(?<kind>D|MS_G|MS_R|MS_RE|MS_NIR)\.(?<ext>JPG|JPEG|TIF|TIFF)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileRegex();

    public static bool TryParse(string fileName, out string captureKey, out string kind)
    {
        var match = FileRegex().Match(Path.GetFileName(fileName));
        if (!match.Success)
        {
            captureKey = "";
            kind = "";
            return false;
        }

        captureKey = match.Groups["key"].Value;
        kind = match.Groups["kind"].Value.ToUpperInvariant();
        return true;
    }

    public static M3mBand? BandFromKind(string kind) =>
        kind.ToUpperInvariant() switch
        {
            "MS_G" => M3mBand.Green,
            "MS_R" => M3mBand.Red,
            "MS_RE" => M3mBand.RedEdge,
            "MS_NIR" => M3mBand.Nir,
            _ => null
        };
}
