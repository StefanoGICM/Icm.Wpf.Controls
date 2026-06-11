using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using FluentAssertions;

namespace Icm.Wpf.Controls.Tests;

public sealed class AutoFilterDataGridTests
{
    [Fact]
    public void InitialFilterValueAttachedPropertyRoundTrips()
    {
        RunOnSta(
            () =>
            {
                DataGridCheckBoxColumn column = new();

                AutoFilterDataGrid.SetInitialFilterValue(column, false);

                AutoFilterDataGrid.GetInitialFilterValue(column).Should().Be(false);
            });
    }

    [Fact]
    public void CanUserFilterDataDefaultsToTrue()
    {
        RunOnSta(
            () =>
            {
                AutoFilterDataGrid grid = new();

                grid.CanUserFilterData.Should().BeTrue();
            });
    }

    [Fact]
    public void FilterButtonVisibilityConverterCollapsesWhenGridFilteringIsDisabled()
    {
        RunOnSta(
            () =>
            {
                AutoFilterDataGridFilterButtonVisibilityConverter converter = new();
                DataGridTextColumn column = new()
                {
                    Binding = new System.Windows.Data.Binding(nameof(Row.Name))
                };

                object result = converter.Convert([false, column], typeof(Visibility), parameter: null!, CultureInfo.InvariantCulture);

                result.Should().Be(Visibility.Collapsed);
            });
    }

    [Fact]
    public void ControlAssemblyDeclaresGenericThemeDictionaryInSourceAssembly()
    {
        ThemeInfoAttribute? themeInfo =
            typeof(AutoFilterDataGrid).Assembly.GetCustomAttribute<ThemeInfoAttribute>();

        themeInfo.Should().NotBeNull();
        themeInfo!.GenericDictionaryLocation.Should().Be(ResourceDictionaryLocation.SourceAssembly);
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        Thread thread = new(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception caught)
                {
                    exception = caught;
                }
            });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
        {
            throw exception;
        }
    }

    private sealed record Row(string Name);
}
