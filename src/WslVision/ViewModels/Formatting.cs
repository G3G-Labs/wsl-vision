using System.Globalization;

namespace WslVision.ViewModels;

public static class Formatting
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static string Bytes(long bytes)
    {
        if (bytes < 0)
            return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }
        var fmt = u == 0 || v >= 100 ? "0" : "0.#";
        return v.ToString(fmt, Pt) + " " + units[u];
    }

    public static string Gb(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.0", Pt) + " GB";

    public static string Percent(double p) => p.ToString(p < 10 ? "0.0" : "0", Pt) + "%";

    public static string Date(DateTimeOffset d)
    {
        var local = d.LocalDateTime;
        var today = DateTime.Today;
        if (local.Date == today)
            return "Hoje, " + local.ToString("HH:mm", Pt);
        if (local.Date == today.AddDays(-1))
            return "Ontem, " + local.ToString("HH:mm", Pt);
        return local.ToString("dd/MM/yyyy HH:mm", Pt);
    }
}
