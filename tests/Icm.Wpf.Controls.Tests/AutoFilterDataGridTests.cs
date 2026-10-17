using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Icm.Wpf.Controls.Tests;

[TestClass]
public sealed class AutoFilterDataGridTests
{
    [STATestMethod]
    public void InitialFilterValueAttachedPropertyRoundTrips()
    {
        DataGridCheckBoxColumn column = new();

        AutoFilterDataGrid.SetInitialFilterValue(column, false);

        AutoFilterDataGrid.GetInitialFilterValue(column).Should().Be(false);
    }

    [STATestMethod]
    public void CanUserFilterDataDefaultsToTrue()
    {
        AutoFilterDataGrid grid = new();

        grid.CanUserFilterData.Should().BeTrue();
    }

    [STATestMethod]
    public void FilterButtonVisibilityConverterCollapsesWhenGridFilteringIsDisabled()
    {
        AutoFilterDataGridFilterButtonVisibilityConverter converter = new();
        DataGridTextColumn column = new()
        {
            Binding = new System.Windows.Data.Binding(nameof(Row.Name))
        };

        object result = converter.Convert([false, column], typeof(Visibility), parameter: null!, CultureInfo.InvariantCulture);

        result.Should().Be(Visibility.Collapsed);
    }

    [TestMethod]
    public void ControlAssemblyDeclaresGenericThemeDictionaryInSourceAssembly()
    {
        ThemeInfoAttribute? themeInfo =
            typeof(AutoFilterDataGrid).Assembly.GetCustomAttribute<ThemeInfoAttribute>();

        themeInfo.Should().NotBeNull();
        themeInfo!.GenericDictionaryLocation.Should().Be(ResourceDictionaryLocation.SourceAssembly);
    }

    private sealed record Row(string Name);
}
