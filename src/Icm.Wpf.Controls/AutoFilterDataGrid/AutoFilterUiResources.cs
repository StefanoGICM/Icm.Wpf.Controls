using System.Globalization;
using System.Resources;

namespace Icm.Wpf.Controls;

public static class AutoFilterUiResources
{
    private static readonly ResourceManager ResourceManager =
        new(
            "Icm.Wpf.Controls.AutoFilterDataGrid.Resources.AutoFilterUiResources",
            typeof(AutoFilterUiResources).Assembly);

    public static string SearchFilterSelectedHeader => GetString(nameof(SearchFilterSelectedHeader));
    public static string EmptyValueText => GetString(nameof(EmptyValueText));
    public static string AutoFilterAllText => GetString(nameof(AutoFilterAllText));
    public static string AutoFilterEmptyText => GetString(nameof(AutoFilterEmptyText));
    public static string AutoFilterClearFiltersText => GetString(nameof(AutoFilterClearFiltersText));

    private static string GetString(string name)
        => ResourceManager.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
