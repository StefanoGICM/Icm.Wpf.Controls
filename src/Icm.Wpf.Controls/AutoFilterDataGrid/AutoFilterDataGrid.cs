using System.Collections;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.Input;

namespace Icm.Wpf.Controls;

public class AutoFilterDataGrid : DataGrid
{
    private static readonly DependencyPropertyDescriptor ContextMenuIsOpenPropertyDescriptor =
        DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
    private static readonly object ClearFiltersMenuItemTag = new();

    public static readonly DependencyProperty CanUserFilterDataProperty =
        DependencyProperty.Register(
            nameof(CanUserFilterData),
            typeof(bool),
            typeof(AutoFilterDataGrid),
            new FrameworkPropertyMetadata(
                true,
                FrameworkPropertyMetadataOptions.Inherits,
                OnCanUserFilterDataChanged));

    public static readonly DependencyProperty HasActiveFilterProperty =
        DependencyProperty.RegisterAttached(
            "HasActiveFilter",
            typeof(bool),
            typeof(AutoFilterDataGrid),
            new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty InitialFilterValueProperty =
        DependencyProperty.RegisterAttached(
            "InitialFilterValue",
            typeof(object),
            typeof(AutoFilterDataGrid),
            new FrameworkPropertyMetadata(null));

    private readonly Dictionary<DataGridColumn, ColumnFilterState> _columnFilters = new();
    private readonly Dictionary<ContextMenu, PendingFilterMenuState> _pendingFilterMenus = new();
    private readonly List<INotifyPropertyChanged> _observedItemPropertySources = new();
    private readonly IRelayCommand<DataGridColumnHeader?> _showColumnFilterMenuCommand;
    private Predicate<object>? _baselineFilter;
    private ICollectionView? _currentView;
    private INotifyCollectionChanged? _observedCollectionChangedSource;

    static AutoFilterDataGrid()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(AutoFilterDataGrid),
            new FrameworkPropertyMetadata(typeof(AutoFilterDataGrid)));
    }

    public AutoFilterDataGrid()
    {
        _showColumnFilterMenuCommand = new RelayCommand<DataGridColumnHeader?>(ShowColumnFilterMenu);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AddHandler(
            FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler(OnContextMenuOpening),
            handledEventsToo: true);
    }

    public bool CanUserFilterData
    {
        get => (bool)GetValue(CanUserFilterDataProperty);
        set => SetValue(CanUserFilterDataProperty, value);
    }

    public IRelayCommand<DataGridColumnHeader?> ShowColumnFilterMenuCommand => _showColumnFilterMenuCommand;

    public static bool GetHasActiveFilter(DataGridColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return (bool)column.GetValue(HasActiveFilterProperty);
    }

    private static void SetHasActiveFilter(DataGridColumn column, bool value)
    {
        ArgumentNullException.ThrowIfNull(column);
        column.SetValue(HasActiveFilterProperty, value);
    }

    public static object? GetInitialFilterValue(DataGridColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.GetValue(InitialFilterValueProperty);
    }

    public static void SetInitialFilterValue(DataGridColumn column, object? value)
    {
        ArgumentNullException.ThrowIfNull(column);
        column.SetValue(InitialFilterValueProperty, value);
    }

    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        DetachFromObservedSource();
        DetachFromCurrentView();
        ResetFilterState(applyInitialFilters: true);
        base.OnItemsSourceChanged(oldValue, newValue);
        AttachToCurrentView();
        AttachToObservedSource();
    }

    private static void OnCanUserFilterDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AutoFilterDataGrid dataGrid)
        {
            dataGrid.ApplyCurrentFilters();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Columns.CollectionChanged -= OnColumnsCollectionChanged;
        Columns.CollectionChanged += OnColumnsCollectionChanged;
        AttachToCurrentView();
        AttachToObservedSource();
        SyncColumnFilters();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Columns.CollectionChanged -= OnColumnsCollectionChanged;
        DetachFromObservedSource();
        DetachFromCurrentView();
    }

    private void OnColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncColumnFilters();
    }

    private void SyncColumnFilters()
    {
        HashSet<DataGridColumn> currentColumns = Columns.ToHashSet();

        foreach (DataGridColumn column in _columnFilters.Keys.Except(currentColumns).ToArray())
        {
            SetHasActiveFilter(column, false);
            _columnFilters.Remove(column);
        }

        foreach (DataGridColumn column in currentColumns)
        {
            if (IsFilterableColumn(column))
            {
                _columnFilters.TryAdd(column, new ColumnFilterState(column));
            }
            else
            {
                _columnFilters.Remove(column);
            }

            SetHasActiveFilter(column, HasActiveFilter(column));
        }

        ApplyConfiguredInitialFilters();
        ApplyCurrentFilters();
    }

    private void ResetFilterState(bool applyInitialFilters)
    {
        foreach (ContextMenu contextMenu in _pendingFilterMenus.Keys.ToArray())
        {
            contextMenu.Closed -= OnFilterMenuClosed;
            ContextMenuIsOpenPropertyDescriptor?.RemoveValueChanged(contextMenu, OnFilterMenuIsOpenChanged);
            contextMenu.IsOpen = false;
        }

        _pendingFilterMenus.Clear();

        foreach (ColumnFilterState state in _columnFilters.Values)
        {
            state.SelectedValues = null;
            state.InitialFilterApplied = false;
        }

        if (applyInitialFilters)
        {
            ApplyConfiguredInitialFilters();
        }

        SyncAttachedFilterStateFlags();
    }

    private void AttachToCurrentView()
    {
        ICollectionView? view = ResolveItemsView();
        if (ReferenceEquals(_currentView, view))
        {
            return;
        }

        _currentView = view;
        _baselineFilter = view is not null && view.CanFilter ? view.Filter : null;
        ApplyCurrentFilters();
    }

    private void DetachFromCurrentView()
    {
        if (_currentView is null || !_currentView.CanFilter)
        {
            _currentView = null;
            _baselineFilter = null;
            return;
        }

        _currentView.Filter = _baselineFilter;
        _currentView.Refresh();
        _currentView = null;
        _baselineFilter = null;
    }

    private void AttachToObservedSource()
    {
        DetachFromObservedSource();
        IEnumerable sourceItems = ResolveSourceItems();

        if (sourceItems is INotifyCollectionChanged collectionChangedSource)
        {
            _observedCollectionChangedSource = collectionChangedSource;
            CollectionChangedEventManager.AddHandler(collectionChangedSource, OnObservedSourceCollectionChanged);
        }

        foreach (object? item in sourceItems)
        {
            if (item is not INotifyPropertyChanged propertyChangedItem ||
                _observedItemPropertySources.Any(existing => ReferenceEquals(existing, propertyChangedItem)))
            {
                continue;
            }

            _observedItemPropertySources.Add(propertyChangedItem);
            PropertyChangedEventManager.AddHandler(propertyChangedItem, OnObservedItemPropertyChanged, string.Empty);
        }
    }

    private void DetachFromObservedSource()
    {
        if (_observedCollectionChangedSource is not null)
        {
            CollectionChangedEventManager.RemoveHandler(_observedCollectionChangedSource, OnObservedSourceCollectionChanged);
            _observedCollectionChangedSource = null;
        }

        foreach (INotifyPropertyChanged propertyChangedItem in _observedItemPropertySources)
        {
            PropertyChangedEventManager.RemoveHandler(propertyChangedItem, OnObservedItemPropertyChanged, string.Empty);
        }

        _observedItemPropertySources.Clear();
    }

    private ICollectionView? ResolveItemsView()
    {
        if (ItemsSource is ICollectionView collectionView)
        {
            return collectionView;
        }

        if (ItemsSource is not null)
        {
            return CollectionViewSource.GetDefaultView(ItemsSource);
        }

        return Items;
    }

    private void OnObservedSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ResetFiltersAfterSourceMutation();
        DetachFromObservedSource();
        AttachToObservedSource();
    }

    private void OnObservedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        ResetFiltersAfterSourceMutation();
    }

    private void ResetFiltersAfterSourceMutation()
    {
        ResetFilterState(applyInitialFilters: true);
        ApplyCurrentFilters();
    }

    private void ShowColumnFilterMenu(DataGridColumnHeader? columnHeader)
    {
        if (columnHeader?.Column is not DataGridColumn column || !CanUserFilterData || !IsFilterableColumn(column))
        {
            return;
        }

        ContextMenu contextMenu = CreateFilterMenu(column);
        columnHeader.ContextMenu = contextMenu;
        contextMenu.PlacementTarget = columnHeader;
        contextMenu.Placement = PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private ContextMenu CreateFilterMenu(DataGridColumn column)
    {
        IReadOnlyList<object?> values = GetVisibleDistinctColumnValues(column);
        ColumnFilterState filterState = _columnFilters.TryGetValue(column, out ColumnFilterState? existingState)
            ? existingState
            : (_columnFilters[column] = new ColumnFilterState(column));

        var contextMenu = new ContextMenu
        {
            MinWidth = Math.Max(120d, column.ActualWidth)
        };
        _pendingFilterMenus[contextMenu] = new PendingFilterMenuState(column, CloneSelectedValues(filterState.SelectedValues));
        ContextMenuIsOpenPropertyDescriptor?.AddValueChanged(contextMenu, OnFilterMenuIsOpenChanged);
        contextMenu.Closed += OnFilterMenuClosed;

        var selectAllItem = new MenuItem
        {
            Header = AutoFilterUiResources.AutoFilterAllText,
            FontWeight = FontWeights.SemiBold,
            StaysOpenOnClick = true
        };
        selectAllItem.Click += (_, _) => SelectAllColumnValues(column, contextMenu);
        contextMenu.Items.Add(selectAllItem);

        var selectEmptyItem = new MenuItem
        {
            Header = AutoFilterUiResources.AutoFilterEmptyText,
            FontWeight = FontWeights.SemiBold,
            StaysOpenOnClick = true
        };
        selectEmptyItem.Click += (_, _) => SelectNoColumnValues(column, contextMenu);
        contextMenu.Items.Add(selectEmptyItem);

        var searchTextBox = new TextBox
        {
            MinWidth = Math.Max(120d, column.ActualWidth - 8d),
            Margin = new Thickness(0, 2, 4, 2)
        };
        searchTextBox.TextChanged += (_, _) => SelectMatchingColumnValues(column, contextMenu, searchTextBox.Text);
        contextMenu.Items.Add(new MenuItem
        {
            Header = searchTextBox,
            StaysOpenOnClick = true,
            Focusable = false
        });

        if (values.Count > 0)
        {
            contextMenu.Items.Add(new Separator());
        }

        foreach (object? value in values)
        {
            var choiceItem = new MenuItem
            {
                Header = FormatFilterValue(value),
                IsCheckable = true,
                IsChecked = filterState.SelectedValues is null || filterState.SelectedValues.Contains(value),
                StaysOpenOnClick = true,
                Tag = new FilterChoice(column, value)
            };
            choiceItem.Click += OnFilterChoiceItemClick;
            contextMenu.Items.Add(choiceItem);
        }

        return contextMenu;
    }

    private void OnFilterChoiceItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not FilterChoice choice)
        {
            return;
        }

        if (ResolveOwningContextMenu(menuItem) is not ContextMenu contextMenu)
        {
            return;
        }

        PendingFilterMenuState pendingState = GetPendingFilterMenuState(contextMenu, choice.Column);
        IReadOnlyList<object?> allValues = GetAllDistinctColumnValues(choice.Column);

        HashSet<object?> selectedValues = pendingState.SelectedValues is null
            ? allValues.ToHashSet()
            : new HashSet<object?>(pendingState.SelectedValues);

        if (menuItem.IsChecked)
        {
            selectedValues.Add(choice.Value);
        }
        else
        {
            selectedValues.Remove(choice.Value);
        }

        pendingState.SelectedValues = selectedValues.Count == allValues.Count
            ? null
            : selectedValues;
        SyncFilterMenuSelectionState(choice.Column, contextMenu);
    }

    private void SelectAllColumnValues(DataGridColumn column, ContextMenu contextMenu)
    {
        PendingFilterMenuState pendingState = GetPendingFilterMenuState(contextMenu, column);
        pendingState.SelectedValues = null;
        SyncFilterMenuSelectionState(column, contextMenu);
    }

    private void SelectNoColumnValues(DataGridColumn column, ContextMenu contextMenu)
    {
        PendingFilterMenuState pendingState = GetPendingFilterMenuState(contextMenu, column);
        pendingState.SelectedValues = new HashSet<object?>();
        SyncFilterMenuSelectionState(column, contextMenu);
    }

    private void SelectMatchingColumnValues(DataGridColumn column, ContextMenu contextMenu, string? filterText)
    {
        string normalizedFilterText = filterText?.Trim() ?? string.Empty;
        if (normalizedFilterText.Length == 0)
        {
            return;
        }

        PendingFilterMenuState pendingState = GetPendingFilterMenuState(contextMenu, column);
        var selectedValues = new HashSet<object?>();

        foreach (MenuItem item in contextMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not FilterChoice choice || !ReferenceEquals(choice.Column, column))
            {
                continue;
            }

            string formattedValue = FormatFilterValue(choice.Value);
            if (formattedValue.Contains(normalizedFilterText, StringComparison.CurrentCultureIgnoreCase))
            {
                selectedValues.Add(choice.Value);
            }
        }

        pendingState.SelectedValues = selectedValues;
        SyncFilterMenuSelectionState(column, contextMenu);
    }

    private void ClearAllFilters()
    {
        ResetFilterState(applyInitialFilters: false);
        ApplyCurrentFilters();
    }

    private void ApplyConfiguredInitialFilters()
    {
        foreach (ColumnFilterState state in _columnFilters.Values)
        {
            if (state.InitialFilterApplied)
            {
                continue;
            }

            state.InitialFilterApplied = true;
            object? configuredValue = GetInitialFilterValue(state.Column);
            if (configuredValue is null)
            {
                continue;
            }

            state.SelectedValues = new HashSet<object?> { CoerceInitialFilterValue(configuredValue) };
        }
    }

    private static object? CoerceInitialFilterValue(object? configuredValue)
    {
        if (configuredValue is string text && bool.TryParse(text, out bool boolValue))
        {
            return boolValue;
        }

        return configuredValue;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindAncestor<ContextMenu>(source) is not null)
        {
            return;
        }

        FrameworkElement? targetElement = ResolveContextMenuTarget(e.OriginalSource as DependencyObject);
        if (targetElement is null)
        {
            return;
        }

        ContextMenu? contextMenu = targetElement.ContextMenu;
        bool hasActiveFilters = _columnFilters.Values.Any(static state => state.SelectedValues is not null);
        if (contextMenu is null)
        {
            if (!hasActiveFilters)
            {
                return;
            }

            contextMenu = new ContextMenu();
            targetElement.ContextMenu = contextMenu;
        }

        EnsureClearFiltersMenuItem(contextMenu);
    }

    private void EnsureClearFiltersMenuItem(ContextMenu contextMenu)
    {
        MenuItem? clearFiltersItem = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(static item => ReferenceEquals(item.Tag, ClearFiltersMenuItemTag));

        if (clearFiltersItem is null)
        {
            if (contextMenu.Items.Count > 0 && contextMenu.Items[^1] is not Separator)
            {
                contextMenu.Items.Add(new Separator());
            }

            clearFiltersItem = new MenuItem
            {
                Header = AutoFilterUiResources.AutoFilterClearFiltersText,
                Tag = ClearFiltersMenuItemTag
            };
            clearFiltersItem.Click += OnClearFiltersContextMenuItemClick;
            contextMenu.Items.Add(clearFiltersItem);
        }

        clearFiltersItem.IsEnabled = _columnFilters.Values.Any(static state => state.SelectedValues is not null);
    }

    private void OnClearFiltersContextMenuItemClick(object sender, RoutedEventArgs e)
    {
        ClearAllFilters();
    }

    private void SyncFilterMenuSelectionState(DataGridColumn column, ContextMenu contextMenu)
    {
        PendingFilterMenuState pendingState = GetPendingFilterMenuState(contextMenu, column);

        foreach (MenuItem item in contextMenu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not FilterChoice choice || !ReferenceEquals(choice.Column, column))
            {
                continue;
            }

            item.IsChecked = pendingState.SelectedValues is null || pendingState.SelectedValues.Contains(choice.Value);
        }
    }

    private void OnFilterMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        CommitPendingFilterMenu(contextMenu);
    }

    private void OnFilterMenuIsOpenChanged(object? sender, EventArgs e)
    {
        if (sender is ContextMenu contextMenu && !contextMenu.IsOpen)
        {
            CommitPendingFilterMenu(contextMenu);
        }
    }

    private void CommitPendingFilterMenu(ContextMenu contextMenu)
    {
        contextMenu.Closed -= OnFilterMenuClosed;
        ContextMenuIsOpenPropertyDescriptor?.RemoveValueChanged(contextMenu, OnFilterMenuIsOpenChanged);

        if (!_pendingFilterMenus.Remove(contextMenu, out PendingFilterMenuState? pendingState))
        {
            return;
        }

        ColumnFilterState state = _columnFilters.TryGetValue(pendingState.Column, out ColumnFilterState? existingState)
            ? existingState
            : (_columnFilters[pendingState.Column] = new ColumnFilterState(pendingState.Column));

        state.SelectedValues = NormalizeSelectedValues(pendingState.Column, pendingState.SelectedValues);
        SetHasActiveFilter(pendingState.Column, HasActiveFilter(pendingState.Column));
        ApplyCurrentFilters();
    }

    private bool HasActiveFilter(DataGridColumn column)
        => _columnFilters.TryGetValue(column, out ColumnFilterState? state) &&
           state.SelectedValues is not null;

    private Predicate<object>? BuildFilterPredicate()
    {
        if (!CanUserFilterData)
        {
            return _baselineFilter;
        }

        bool hasAnyActiveFilters = _columnFilters.Values.Any(static state => state.SelectedValues is not null);
        if (!hasAnyActiveFilters)
        {
            return _baselineFilter;
        }

        return item => ItemMatchesFilters(item);
    }

    private void ApplyCurrentFilters()
    {
        SyncAttachedFilterStateFlags();

        if (_currentView is null || !_currentView.CanFilter)
        {
            return;
        }

        _currentView.Filter = BuildFilterPredicate();
        _currentView.Refresh();
    }

    private void SyncAttachedFilterStateFlags()
    {
        foreach (DataGridColumn column in Columns)
        {
            SetHasActiveFilter(column, HasActiveFilter(column));
        }
    }

    private bool ItemMatchesFilters(object item, DataGridColumn? excludedColumn = null)
    {
        if (_baselineFilter is not null && !_baselineFilter(item))
        {
            return false;
        }

        foreach (ColumnFilterState state in _columnFilters.Values)
        {
            if (ReferenceEquals(state.Column, excludedColumn))
            {
                continue;
            }

            if (state.SelectedValues is null)
            {
                continue;
            }

            object? value = TryExtractBoundValue(item, state.Column);
            if (!state.SelectedValues.Contains(value))
            {
                return false;
            }
        }

        return true;
    }

    private object?[] GetVisibleDistinctColumnValues(DataGridColumn column)
        => GetDistinctColumnValues(column, item => ItemMatchesFilters(item, excludedColumn: column));

    private object?[] GetAllDistinctColumnValues(DataGridColumn column)
        => GetDistinctColumnValues(column, static _ => true);

    private object?[] GetDistinctColumnValues(DataGridColumn column, Predicate<object> itemPredicate)
    {
        IEnumerable sourceItems = ResolveSourceItems();
        var values = new List<object?>();

        foreach (object? item in sourceItems)
        {
            if (item is null)
            {
                continue;
            }

            if (!itemPredicate(item))
            {
                continue;
            }

            object? value = TryExtractBoundValue(item, column);
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        if (values.Count == 0)
        {
            return Array.Empty<object?>();
        }

        return values
            .OrderBy(FormatFilterValue, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private IEnumerable ResolveSourceItems()
    {
        if (ItemsSource is CollectionView itemsSourceCollectionView)
        {
            return itemsSourceCollectionView.SourceCollection;
        }

        if (_currentView is CollectionView collectionView)
        {
            return collectionView.SourceCollection;
        }

        return ItemsSource ?? Items;
    }

    internal static bool IsFilterableColumn(DataGridColumn? column)
        => ResolveColumnBinding(column) is Binding binding &&
           !string.IsNullOrWhiteSpace(binding.Path?.Path);

    internal static Binding? ResolveColumnBinding(DataGridColumn? column)
    {
        return column switch
        {
            DataGridComboBoxColumn comboBoxColumn when comboBoxColumn.SelectedItemBinding is Binding binding &&
                                                       !string.IsNullOrWhiteSpace(binding.Path?.Path) => binding,
            DataGridBoundColumn boundColumn when boundColumn.Binding is Binding binding &&
                                               !string.IsNullOrWhiteSpace(binding.Path?.Path) => binding,
            _ => null
        };
    }

    internal static string FormatFilterValue(object? value)
    {
        if (value is null)
        {
            return AutoFilterUiResources.EmptyValueText;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
        {
            return AutoFilterUiResources.EmptyValueText;
        }

        return value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture),
            _ => value.ToString() ?? AutoFilterUiResources.EmptyValueText
        };
    }

    private static object? TryExtractBoundValue(object item, DataGridColumn column)
    {
        Binding? binding = ResolveColumnBinding(column);
        if (binding?.Path?.Path is not string path || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        object? current = item;
        foreach (string segment in SplitPathSegments(path))
        {
            current = ResolvePathSegment(current, segment);
            if (current is null)
            {
                return null;
            }
        }

        if (binding.Converter is not null)
        {
            return binding.Converter.Convert(
                current,
                typeof(object),
                binding.ConverterParameter,
                binding.ConverterCulture ?? CultureInfo.CurrentCulture);
        }

        return current;
    }

    private static List<string> SplitPathSegments(string path)
    {
        var segments = new List<string>();
        int bracketDepth = 0;
        int startIndex = 0;

        for (int index = 0; index < path.Length; index++)
        {
            char current = path[index];
            if (current == '[')
            {
                bracketDepth++;
            }
            else if (current == ']')
            {
                bracketDepth = Math.Max(0, bracketDepth - 1);
            }
            else if (current == '.' && bracketDepth == 0)
            {
                segments.Add(path[startIndex..index]);
                startIndex = index + 1;
            }
        }

        segments.Add(path[startIndex..]);
        return segments;
    }

    private static object? ResolvePathSegment(object? current, string segment)
    {
        if (current is null || string.IsNullOrWhiteSpace(segment))
        {
            return null;
        }

        int indexerStart = segment.IndexOf('[');
        string propertyName = indexerStart >= 0 ? segment[..indexerStart] : segment;

        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            PropertyDescriptor? propertyDescriptor = TypeDescriptor.GetProperties(current).Find(propertyName, ignoreCase: false);
            if (propertyDescriptor is null)
            {
                return null;
            }

            current = propertyDescriptor.GetValue(current);
        }

        if (indexerStart < 0 || current is null)
        {
            return current;
        }

        int indexerEnd = segment.LastIndexOf(']');
        if (indexerEnd <= indexerStart)
        {
            return current;
        }

        string indexerValue = segment[(indexerStart + 1)..indexerEnd];
        return ResolveIndexer(current, indexerValue);
    }

    private static object? ResolveIndexer(object current, string indexerValue)
    {
        if (current is IDictionary dictionary)
        {
            return dictionary.Contains(indexerValue) ? dictionary[indexerValue] : null;
        }

        return current.GetType()
            .GetProperty("Item", new[] { typeof(string) })
            ?.GetValue(current, new object[] { indexerValue });
    }

    private static ContextMenu? ResolveOwningContextMenu(MenuItem menuItem)
        => menuItem.Parent as ContextMenu ??
           ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;

    private FrameworkElement? ResolveContextMenuTarget(DependencyObject? source)
    {
        if (FindAncestor<DataGridColumnHeader>(source) is DataGridColumnHeader columnHeader)
        {
            return columnHeader;
        }

        if (FindAncestor<DataGridRow>(source) is DataGridRow dataGridRow)
        {
            return dataGridRow;
        }

        return this;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current switch
            {
                Visual or Visual3D => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return null;
    }

    private PendingFilterMenuState GetPendingFilterMenuState(ContextMenu contextMenu, DataGridColumn column)
    {
        if (_pendingFilterMenus.TryGetValue(contextMenu, out PendingFilterMenuState? pendingState) &&
            ReferenceEquals(pendingState.Column, column))
        {
            return pendingState;
        }

        ColumnFilterState state = _columnFilters.TryGetValue(column, out ColumnFilterState? existingState)
            ? existingState
            : (_columnFilters[column] = new ColumnFilterState(column));

        pendingState = new PendingFilterMenuState(column, CloneSelectedValues(state.SelectedValues));
        _pendingFilterMenus[contextMenu] = pendingState;
        return pendingState;
    }

    private HashSet<object?>? NormalizeSelectedValues(DataGridColumn column, HashSet<object?>? selectedValues)
    {
        if (selectedValues is null)
        {
            return null;
        }

        IReadOnlyList<object?> allValues = GetAllDistinctColumnValues(column);
        if (selectedValues.Count == allValues.Count)
        {
            return null;
        }

        return new HashSet<object?>(selectedValues);
    }

    private static HashSet<object?>? CloneSelectedValues(HashSet<object?>? selectedValues)
        => selectedValues is null ? null : new HashSet<object?>(selectedValues);

    private sealed record FilterChoice(DataGridColumn Column, object? Value);

    private sealed class PendingFilterMenuState
    {
        public PendingFilterMenuState(DataGridColumn column, HashSet<object?>? selectedValues)
        {
            Column = column;
            SelectedValues = selectedValues;
        }

        public DataGridColumn Column { get; }

        public HashSet<object?>? SelectedValues { get; set; }
    }

    private sealed class ColumnFilterState
    {
        public ColumnFilterState(DataGridColumn column)
        {
            Column = column;
        }

        public DataGridColumn Column { get; }

        public HashSet<object?>? SelectedValues { get; set; }

        public bool InitialFilterApplied { get; set; }
    }
}

public sealed class AutoFilterDataGridFilterButtonVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 2)
        {
            return Visibility.Collapsed;
        }

        bool canFilter = values[0] is true;
        bool isFilterableColumn = values[1] is DataGridColumn column && AutoFilterDataGrid.IsFilterableColumn(column);
        return canFilter && isFilterableColumn ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
