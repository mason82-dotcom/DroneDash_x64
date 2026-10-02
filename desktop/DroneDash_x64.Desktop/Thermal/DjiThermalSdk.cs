using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DroneDash_x64.Desktop.Models;

namespace DroneDash_x64.Desktop.Thermal;

public enum ThermalPalette
{
    WhiteHot = 0,
    Fulgurite = 1,
    IronRed = 2,
    HotIron = 3,
    Medical = 4,
    Arctic = 5,
    Rainbow1 = 6,
    Rainbow2 = 7,
    Tint = 8,
    BlackHot = 9
}

public sealed record ThermalAnalysisResult(
    int Width,
    int Height,
    float MinimumC,
    int MinimumX,
    int MinimumY,
    float MaximumC,
    int MaximumX,
    int MaximumY,
    float AverageC,
    float CenterC,
    float DistanceM,
    float Humidity,
    float Emissivity,
    float ReflectionC,
    float AmbientTemperatureC,
    string ApiVersion,
    string RjpegVersion,
    BitmapSource PseudoColorImage,
    float[] Temperatures)
{
    public float? TemperatureAt(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return null;

        var value = Temperatures[(y * Width) + x];
        return float.IsFinite(value) ? value : null;
    }

    public IReadOnlyList<ImageMetadataEntryDto> ToMetadata() =>
    [
        new("Thermal", "DJI Thermal SDK", "v1.8"),
        new("Thermal", "DIRP API", ApiVersion),
        new("Thermal", "R-JPEG Version", RjpegVersion),
        new("Thermal", "Auflösung", $"{Width} × {Height}"),
        new("Thermal", "Minimum", $"{MinimumC:F2} °C @ {MinimumX},{MinimumY}"),
        new("Thermal", "Maximum", $"{MaximumC:F2} °C @ {MaximumX},{MaximumY}"),
        new("Thermal", "Mittelwert", $"{AverageC:F2} °C"),
        new("Thermal", "Bildmitte", $"{CenterC:F2} °C"),
        new("Thermal Parameter", "Distanz", $"{DistanceM:F2} m"),
        new("Thermal Parameter", "Luftfeuchte", $"{Humidity:F2}"),
        new("Thermal Parameter", "Emissivität", $"{Emissivity:F3}"),
        new("Thermal Parameter", "Reflexion", $"{ReflectionC:F2} °C"),
        new("Thermal Parameter", "Umgebung", $"{AmbientTemperatureC:F2} °C")
    ];
}

public sealed class ThermalSdkException : Exception
{
    public ThermalSdkException(string operation, int code)
        : base($"{operation}: DJI DIRP {code} ({DjiThermalSdk.ErrorName(code)})")
    {
        Operation = operation;
        Code = code;
    }

    public string Operation { get; }
    public int Code { get; }
}

public sealed class DjiThermalSdk : IDisposable
{
    public const string SupportedSdkVersion = "1.8";

    private const string LibraryName = "libdirp.dll";
    private const int Success = 0;
    private const int MaximumPixels = 100_000_000;

    private IntPtr _library;
    private bool _disposed;

    private CreateFromRjpegDelegate? _createFromRjpeg;
    private DestroyDelegate? _destroy;
    private GetApiVersionDelegate? _getApiVersion;
    private GetRjpegVersionDelegate? _getRjpegVersion;
    private GetResolutionDelegate? _getResolution;
    private GetMeasurementParamsDelegate? _getMeasurementParams;
    private MeasureExDelegate? _measureEx;
    private SetPseudoColorDelegate? _setPseudoColor;
    private ProcessDelegate? _process;

    public DjiThermalSdk()
    {
        ReleaseDirectory = ResolveReleaseDirectory();
        if (ReleaseDirectory is null)
        {
            Status = "DJI Thermal SDK v1.8 nicht installiert.";
            return;
        }

        var libraryPath = Path.Combine(ReleaseDirectory, LibraryName);
        try
        {
            if (!SetDllDirectory(ReleaseDirectory))
                throw new InvalidOperationException($"DLL-Suchpfad konnte nicht gesetzt werden: {Marshal.GetLastWin32Error()}");

            _library = NativeLibrary.Load(libraryPath);
            BindExports();
            IsAvailable = true;
            Status = $"DJI Thermal SDK v{SupportedSdkVersion} bereit · {ReleaseDirectory}";
        }
        catch (Exception ex)
        {
            Status = $"DJI Thermal SDK gefunden, aber nicht ladbar: {ex.Message}";
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }
        }
    }

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; }
    public string? ReleaseDirectory { get; }

    public ThermalAnalysisResult Analyze(string path, ThermalPalette palette)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAvailable ||
            _createFromRjpeg is null ||
            _destroy is null ||
            _getApiVersion is null ||
            _getRjpegVersion is null ||
            _getResolution is null ||
            _getMeasurementParams is null ||
            _measureEx is null ||
            _setPseudoColor is null ||
            _process is null)
        {
            throw new InvalidOperationException(Status);
        }

        var input = File.ReadAllBytes(path);
        if (input.Length == 0)
            throw new InvalidDataException("Thermal-Datei ist leer.");
        if (input.LongLength > int.MaxValue)
            throw new InvalidDataException("R-JPEG überschreitet das DJI-DIRP-int32-Größenlimit.");

        var inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        IntPtr handle = IntPtr.Zero;

        try
        {
            Check("dirp_create_from_rjpeg",
                _createFromRjpeg(inputPin.AddrOfPinnedObject(), input.Length, out handle));

            Check("dirp_get_rjpeg_resolution", _getResolution(handle, out var resolution));
            ValidateResolution(resolution.Width, resolution.Height);

            Check("dirp_get_api_version", _getApiVersion(handle, out var apiVersion));
            Check("dirp_get_rjpeg_version", _getRjpegVersion(handle, out var rjpegVersion));
            Check("dirp_get_measurement_params", _getMeasurementParams(handle, out var measurementParams));
            Check("dirp_set_pseudo_color", _setPseudoColor(handle, (int)palette));

            var pixelCountLong = (long)resolution.Width * resolution.Height;
            var pixelCount = checked((int)pixelCountLong);
            var temperatureBytes = checked(pixelCount * sizeof(float));
            var rgbBytes = checked(pixelCount * 3);

            var temperatures = new float[pixelCount];
            var temperaturePin = GCHandle.Alloc(temperatures, GCHandleType.Pinned);
            try
            {
                Check("dirp_measure_ex",
                    _measureEx(handle, temperaturePin.AddrOfPinnedObject(), temperatureBytes));
            }
            finally
            {
                temperaturePin.Free();
            }

            var rgb = new byte[rgbBytes];
            var rgbPin = GCHandle.Alloc(rgb, GCHandleType.Pinned);
            try
            {
                Check("dirp_process", _process(handle, rgbPin.AddrOfPinnedObject(), rgbBytes));
            }
            finally
            {
                rgbPin.Free();
            }

            var statistics = CalculateStatistics(temperatures, resolution.Width, resolution.Height);

            var bitmap = BitmapSource.Create(
                resolution.Width,
                resolution.Height,
                96,
                96,
                PixelFormats.Rgb24,
                null,
                rgb,
                resolution.Width * 3);
            bitmap.Freeze();

            return new ThermalAnalysisResult(
                resolution.Width,
                resolution.Height,
                statistics.MinimumC,
                statistics.MinimumX,
                statistics.MinimumY,
                statistics.MaximumC,
                statistics.MaximumX,
                statistics.MaximumY,
                statistics.AverageC,
                statistics.CenterC,
                measurementParams.Distance,
                measurementParams.Humidity,
                measurementParams.Emissivity,
                measurementParams.Reflection,
                measurementParams.AmbientTemperature,
                FormatApiVersion(apiVersion),
                $"{rjpegVersion.Rjpeg}/{rjpegVersion.Header}/{rjpegVersion.Curve}",
                bitmap,
                temperatures);
        }
        finally
        {
            if (handle != IntPtr.Zero && _destroy is not null)
            {
                try
                {
                    _destroy(handle);
                }
                catch
                {
                    // Native cleanup must not mask the original result.
                }
            }

            inputPin.Free();
        }
    }

    private void BindExports()
    {
        _createFromRjpeg = Bind<CreateFromRjpegDelegate>("dirp_create_from_rjpeg");
        _destroy = Bind<DestroyDelegate>("dirp_destroy");
        _getApiVersion = Bind<GetApiVersionDelegate>("dirp_get_api_version");
        _getRjpegVersion = Bind<GetRjpegVersionDelegate>("dirp_get_rjpeg_version");
        _getResolution = Bind<GetResolutionDelegate>("dirp_get_rjpeg_resolution");
        _getMeasurementParams = Bind<GetMeasurementParamsDelegate>("dirp_get_measurement_params");
        _measureEx = Bind<MeasureExDelegate>("dirp_measure_ex");
        _setPseudoColor = Bind<SetPseudoColorDelegate>("dirp_set_pseudo_color");
        _process = Bind<ProcessDelegate>("dirp_process");
    }

    private T Bind<T>(string export) where T : Delegate
    {
        var address = NativeLibrary.GetExport(_library, export);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static string? ResolveReleaseDirectory()
    {
        var roots = new List<string>();

        var configured = Environment.GetEnvironmentVariable("DJI_TSDK_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            roots.Add(configured);

        roots.Add(Path.Combine(AppContext.BaseDirectory, "thermal-sdk"));
        roots.Add(Path.Combine(AppContext.BaseDirectory, "third_party", "dji-tsdk", "runtime"));
        roots.Add(Path.Combine(
            Environment.CurrentDirectory,
            "desktop",
            "DroneDash_x64.Desktop",
            "third_party",
            "dji-tsdk",
            "runtime"));

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = FindReleaseDirectory(root);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private static string? FindReleaseDirectory(string root)
    {
        if (!Directory.Exists(root))
            return null;

        if (File.Exists(Path.Combine(root, LibraryName)))
            return Path.GetFullPath(root);

        try
        {
            var candidates = Directory
                .EnumerateFiles(root, LibraryName, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => directory!)
                .Where(directory =>
                    directory.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                    directory.Contains("release_x64", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return candidates.Count == 1 ? Path.GetFullPath(candidates[0]) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ValidateResolution(int width, int height)
    {
        var pixels = (long)width * height;
        if (width <= 0 || height <= 0 || pixels <= 0 || pixels > MaximumPixels)
            throw new InvalidDataException($"Ungültige Thermal-Auflösung {width} × {height}.");
        if (pixels * sizeof(float) > int.MaxValue || pixels * 3 > int.MaxValue)
            throw new InvalidDataException("Thermal-Bild überschreitet das DJI-DIRP-Pufferlimit.");
    }

    private static ThermalStatistics CalculateStatistics(float[] values, int width, int height)
    {
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        var minimumIndex = -1;
        var maximumIndex = -1;
        double sum = 0;
        var count = 0;

        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!float.IsFinite(value))
                continue;

            if (value < minimum)
            {
                minimum = value;
                minimumIndex = index;
            }

            if (value > maximum)
            {
                maximum = value;
                maximumIndex = index;
            }

            sum += value;
            count++;
        }

        if (count == 0 || minimumIndex < 0 || maximumIndex < 0)
            throw new InvalidDataException("DJI TSDK lieferte keine gültigen Temperaturpixel.");

        var centerIndex = (height / 2) * width + (width / 2);
        var center = values[Math.Clamp(centerIndex, 0, values.Length - 1)];

        return new ThermalStatistics(
            minimum,
            minimumIndex % width,
            minimumIndex / width,
            maximum,
            maximumIndex % width,
            maximumIndex / width,
            (float)(sum / count),
            center);
    }

    private static string FormatApiVersion(DirpApiVersion version)
    {
        var bytes = BitConverter.GetBytes(version.Magic);
        var magic = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(magic)
            ? version.Api.ToString()
            : $"{version.Api} · {magic}";
    }

    private static void Check(string operation, int code)
    {
        if (code != Success)
            throw new ThermalSdkException(operation, code);
    }

    internal static string ErrorName(int code) =>
        code switch
        {
            0 => "SUCCESS",
            -1 => "MALLOC",
            -2 => "POINTER_NULL",
            -3 => "INVALID_PARAMS",
            -4 => "INVALID_RAW",
            -5 => "INVALID_HEADER",
            -6 => "INVALID_CURVE",
            -7 => "RJPEG_PARSE",
            -8 => "SIZE",
            -9 => "INVALID_HANDLE",
            -10 => "FORMAT_INPUT",
            -11 => "FORMAT_OUTPUT",
            -12 => "UNSUPPORTED_FUNC",
            -13 => "NOT_READY",
            -14 => "ACTIVATION",
            -15 => "INVALID_INI",
            -16 => "INVALID_SUB_DLL",
            -32 => "ADVANCED",
            -64 => "SUPER_MODE",
            _ => "UNKNOWN"
        };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsAvailable = false;

        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DirpApiVersion
    {
        public uint Api;
        public ulong Magic;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DirpRjpegVersion
    {
        public uint Rjpeg;
        public uint Header;
        public uint Curve;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DirpResolution
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DirpMeasurementParams
    {
        public float Distance;
        public float Humidity;
        public float Emissivity;
        public float Reflection;
        public float AmbientTemperature;
    }

    private sealed record ThermalStatistics(
        float MinimumC,
        int MinimumX,
        int MinimumY,
        float MaximumC,
        int MaximumX,
        int MaximumY,
        float AverageC,
        float CenterC);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateFromRjpegDelegate(IntPtr data, int size, out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApiVersionDelegate(IntPtr handle, out DirpApiVersion version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetRjpegVersionDelegate(IntPtr handle, out DirpRjpegVersion version);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetResolutionDelegate(IntPtr handle, out DirpResolution resolution);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetMeasurementParamsDelegate(IntPtr handle, out DirpMeasurementParams parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MeasureExDelegate(IntPtr handle, IntPtr temperatureImage, int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetPseudoColorDelegate(IntPtr handle, int palette);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProcessDelegate(IntPtr handle, IntPtr colorImage, int size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);
}
