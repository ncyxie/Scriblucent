using System.Runtime.InteropServices;
using Scriblucent.Models;

namespace Scriblucent.Services;

internal static class MeasurementUnitService
{
    internal static string Resolve(string? preference)
    {
        var normalized = AppSettings.NormalizeMeasurementUnitSystem(preference);
        if (normalized != AppSettings.UnitSystemSystem) return normalized;

        // Read the user's Windows regional override, independently of the app's
        // language. LOCALE_NAME_USER_DEFAULT is null; the numeric buffer is two WCHARs.
        const uint localeMeasure = 0x0000000D, returnNumber = 0x20000000;
        return GetLocaleInfoEx(null, localeMeasure | returnNumber, out var measure, 2) != 0 && measure == 0
            ? AppSettings.UnitSystemMetric : AppSettings.UnitSystemImperial;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetLocaleInfoEx(string? localeName, uint localeType, out uint data, int characterCount);
}
