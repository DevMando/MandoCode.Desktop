using MandoCode.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace MandoCode.Desktop;

public sealed partial class MainWindow
{
    private static IReadOnlyList<TagChip> TagChips(IEnumerable<string> assignments, IReadOnlyList<ItemTag> definitions) =>
        assignments.Select(name => definitions.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(tag => tag != null)
            .Select(tag => new TagChip { Name = tag!.Name, Brush = new SolidColorBrush(ParseTagColor(tag.Color)) })
            .ToList();

    private static Color ParseTagColor(string value)
    {
        var hex = value.Trim().TrimStart('#');
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb) && hex.Length == 6
            ? Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : Color.FromArgb(255, 59, 130, 246);
    }

    private void OpenRowTagMenu(Button anchor, TagScope scope, string itemKey)
    {
        var selected = _itemTags.GetItemTags(scope, itemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menu = new MenuFlyout();

        foreach (var tag in _itemTags.GetTags(scope))
        {
            var item = new ToggleMenuFlyoutItem { Text = tag.Name, IsChecked = selected.Contains(tag.Name) };
            item.Click += async (_, _) =>
            {
                if (item.IsChecked) selected.Add(tag.Name);
                else selected.Remove(tag.Name);
                _itemTags.SetItemTags(scope, itemKey, selected);
                if (scope == TagScope.Mcps) await RefreshMcpListAsync();
                else RefreshSkillsList();
            };
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Manage tags…" };
        manage.Click += async (_, _) =>
        {
            await ShowTagManagerAsync(scope, scope == TagScope.Mcps ? "MCP tags" : "Skill tags");
            if (scope == TagScope.Mcps) await RefreshMcpListAsync();
            else RefreshSkillsList();
        };
        menu.Items.Add(manage);
        menu.ShowAt(anchor);
    }

    private void McpRowTags_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: McpRow row } button)
            OpenRowTagMenu(button, TagScope.Mcps, row.Name);
    }

    private void SkillRowTags_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SkillRow row } button)
            OpenRowTagMenu(button, TagScope.Skills, row.FolderPath);
    }
    private bool _managementToolbarLayoutQueued;

    /// <summary>Keep the action and filter clusters on one row whenever they genuinely fit. Once
    /// their measured widths would collide, move the complete Filters cluster below the actions
    /// rather than shrinking, overlapping, or wrapping individual filter controls unpredictably.</summary>
    private void ManagementToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_managementToolbarLayoutQueued) return;
        _managementToolbarLayoutQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _managementToolbarLayoutQueued = false;
            LayoutManagementToolbar(McpToolbar, McpActions, McpFilters);
            LayoutManagementToolbar(SkillToolbar, SkillActions, SkillFilters);
        });
    }

    private static void LayoutManagementToolbar(Grid toolbar, FrameworkElement actions, FrameworkElement filters)
    {
        if (toolbar.ActualWidth <= 0) return;
        const double spacing = 16;
        var needsSecondRow = actions.DesiredSize.Width + filters.DesiredSize.Width + spacing > toolbar.ActualWidth;
        var desiredRow = needsSecondRow ? 1 : 0;
        var desiredColumn = needsSecondRow ? 0 : 1;
        var desiredSpan = needsSecondRow ? 2 : 1;

        if (Grid.GetRow(filters) == desiredRow && Grid.GetColumn(filters) == desiredColumn &&
            Grid.GetColumnSpan(filters) == desiredSpan) return;

        Grid.SetRow(filters, desiredRow);
        Grid.SetColumn(filters, desiredColumn);
        Grid.SetColumnSpan(filters, desiredSpan);
        filters.HorizontalAlignment = needsSecondRow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        filters.Margin = needsSecondRow ? new Thickness(0, 8, 0, 0) : new Thickness(0);
    }

    private sealed record TagColorChoice(string Name, string Hex)
    {
        public SolidColorBrush Brush { get; } = new(ParseTagColor(Hex));
    }

    private sealed record TagDisplay(string Name, string Color)
    {
        public SolidColorBrush Brush { get; } = new(ParseTagColor(Color));
    }

    private static DataTemplate TagColorTemplate() => (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <StackPanel Orientation="Horizontal" Spacing="7">
                <Ellipse Width="12" Height="12" Fill="{Binding Brush}" VerticalAlignment="Center"/>
                <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
            </StackPanel>
        </DataTemplate>
        """);

    /// <summary>Shows the catalog for one management surface. Assignment is handled directly from
    /// each row; this modal owns the reusable names and colors exposed by that surface's filter.</summary>
    private async Task ShowTagManagerAsync(TagScope scope, string title)
    {
        var input = new TextBox
        {
            PlaceholderText = "New tag (for example: database)",
            MinWidth = 300,
        };
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MinHeight = 120,
            MaxHeight = 260,
            ItemTemplate = TagColorTemplate(),
        };
        var colors = new List<TagColorChoice>
        {
            new("Blue", "#3B82F6"), new("Green", "#22C55E"), new("Gold", "#EAB308"),
            new("Orange", "#F97316"), new("Red", "#EF4444"), new("Purple", "#A855F7"),
            new("Pink", "#EC4899"), new("Slate", "#64748B"),
        };
        var color = new ComboBox
        {
            ItemsSource = colors,
            ItemTemplate = TagColorTemplate(),
            SelectedIndex = 0,
            MinWidth = 130,
        };
        var add = new Button { Content = "Add tag" };
        var remove = new Button { Content = "Remove selected" };

        void Refresh()
        {
            list.ItemsSource = _itemTags.GetTags(scope)
                .Select(tag => new TagDisplay(tag.Name, tag.Color))
                .ToList();
            remove.IsEnabled = list.SelectedItem is TagDisplay;
        }

        list.SelectionChanged += (_, _) => remove.IsEnabled = list.SelectedItem is TagDisplay;
        add.Click += (_, _) =>
        {
            _itemTags.AddTag(scope, input.Text, (color.SelectedItem as TagColorChoice)?.Hex ?? "#3B82F6");
            input.Text = "";
            Refresh();
            input.Focus(FocusState.Programmatic);
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is TagDisplay tag)
            {
                _itemTags.DeleteTag(scope, tag.Name);
                Refresh();
            }
        };

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Tags are available only on this page type. Removing a tag also removes it from items using it.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addRow.Children.Add(input);
        addRow.Children.Add(color);
        addRow.Children.Add(add);
        content.Children.Add(addRow);
        content.Children.Add(list);
        content.Children.Add(remove);
        Refresh();

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

}
