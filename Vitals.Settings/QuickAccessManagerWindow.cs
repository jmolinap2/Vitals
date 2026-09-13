using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Vitals.Shared;

namespace Vitals.Settings;

internal sealed class QuickAccessManagerWindow : Window
{
    private readonly ObservableCollection<QuickAccessItem> _items;
    private readonly ListBox _list;
    private readonly TextBlock _emptyState;

    private static readonly Brush BackgroundBrush = BrushFrom("#0F1116");
    private static readonly Brush CardBrush = BrushFrom("#171A21");
    private static readonly Brush RowBrush = BrushFrom("#1D212A");
    private static readonly Brush TextBrush = BrushFrom("#EAF0F5");
    private static readonly Brush MutedBrush = BrushFrom("#8A97A6");
    private static readonly Brush AccentBrush = BrushFrom("#2AD1BE");
    private static readonly Brush BorderBrush = BrushFrom("#2A303B");

    public QuickAccessManagerWindow()
    {
        Title = "Accesos rápidos de Vitals";
        Width = 820;
        Height = 590;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = BackgroundBrush;
        Foreground = TextBrush;

        var config = VitalsConfig.Load();
        _items = new ObservableCollection<QuickAccessItem>(
            config.QuickAccess.OrderBy(x => x.Order).Select(Clone));

        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = "Accesos rápidos",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
        });
        title.Children.Add(new TextBlock
        {
            Text = "Agrega programas, archivos, carpetas, URLs o scripts al menú de la bandeja de Vitals.",
            FontSize = 12.5,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 4, 0, 0),
        });
        header.Children.Add(title);

        var addButton = CreateButton("+  Agregar acceso", primary: true);
        addButton.Click += (_, _) => AddItem();
        Grid.SetColumn(addButton, 1);
        header.Children.Add(addButton);

        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
        };
        Grid.SetRow(card, 1);
        root.Children.Add(card);

        var cardGrid = new Grid();
        card.Child = cardGrid;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TextBrush,
            ItemsSource = _items,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        _list.MouseDoubleClick += (_, _) => EditSelected();
        _list.ItemTemplate = BuildItemTemplate();
        cardGrid.Children.Add(_list);

        _emptyState = new TextBlock
        {
            Text = "Todavía no hay accesos rápidos.\nAgrega uno para verlo en el menú de la bandeja.",
            Foreground = MutedBrush,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cardGrid.Children.Add(_emptyState);
        RefreshEmptyState();

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        footer.Children.Add(new TextBlock
        {
            Text = "Los cambios se aplican al guardar y Vitals recarga el menú sin reiniciarse.",
            Foreground = MutedBrush,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var closeButton = CreateButton("Cancelar", primary: false);
        closeButton.Margin = new Thickness(0, 0, 10, 0);
        closeButton.Click += (_, _) => Close();
        actions.Children.Add(closeButton);

        var saveButton = CreateButton("Guardar cambios", primary: true);
        saveButton.Click += (_, _) => SaveAndClose();
        actions.Children.Add(saveButton);
    }

    private DataTemplate BuildItemTemplate()
    {
        var template = new DataTemplate(typeof(QuickAccessItem));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, RowBrush);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 10, 10, 10));
        border.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 7));

        var grid = new FrameworkElementFactory(typeof(Grid));
        grid.AppendChild(Column("Auto"));
        grid.AppendChild(Column("*"));
        grid.AppendChild(Column("Auto"));
        border.AppendChild(grid);

        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.TextProperty, "↗");
        icon.SetValue(TextBlock.FontSizeProperty, 19d);
        icon.SetValue(TextBlock.ForegroundProperty, AccentBrush);
        icon.SetValue(FrameworkElement.WidthProperty, 34d);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(icon);

        var info = new FrameworkElementFactory(typeof(StackPanel));
        info.SetValue(Grid.ColumnProperty, 1);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickAccessItem.Name)));
        name.SetValue(TextBlock.ForegroundProperty, TextBrush);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.FontSizeProperty, 13.5d);
        info.AppendChild(name);

        var target = new FrameworkElementFactory(typeof(TextBlock));
        target.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickAccessItem.Target)));
        target.SetValue(TextBlock.ForegroundProperty, MutedBrush);
        target.SetValue(TextBlock.FontSizeProperty, 11d);
        target.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        target.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        info.AppendChild(target);
        grid.AppendChild(info);

        var enabled = new FrameworkElementFactory(typeof(CheckBox));
        enabled.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(QuickAccessItem.Enabled)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        enabled.SetValue(Grid.ColumnProperty, 2);
        enabled.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        enabled.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 4, 0));
        enabled.SetValue(ContentControl.ContentProperty, "Activo");
        enabled.SetValue(Control.ForegroundProperty, MutedBrush);
        grid.AppendChild(enabled);

        template.VisualTree = border;
        return template;
    }

    private static FrameworkElementFactory Column(string width)
    {
        var c = new FrameworkElementFactory(typeof(ColumnDefinition));
        c.SetValue(ColumnDefinition.WidthProperty,
            width == "*" ? new GridLength(1, GridUnitType.Star) : GridLength.Auto);
        return c;
    }

    private void AddItem()
    {
        var dialog = new QuickAccessEditorWindow(null) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;
        dialog.Value.Order = _items.Count;
        _items.Add(dialog.Value);
        _list.SelectedItem = dialog.Value;
        RefreshEmptyState();
    }

    private void EditSelected()
    {
        if (_list.SelectedItem is not QuickAccessItem selected) return;
        var dialog = new QuickAccessEditorWindow(Clone(selected)) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;

        int index = _items.IndexOf(selected);
        dialog.Value.Order = selected.Order;
        _items[index] = dialog.Value;
        _list.SelectedIndex = index;
    }

    private void SaveAndClose()
    {
        var config = VitalsConfig.Load();
        config.QuickAccess = _items.Select((item, index) =>
        {
            var copy = Clone(item);
            copy.Order = index;
            return copy;
        }).ToList();
        config.Save();

        nint hwnd = NativeInterop.FindWindow("VitalsPillWindow", null);
        if (hwnd != 0)
            NativeInterop.PostMessage(hwnd, NativeInterop.WM_RELOAD_CONFIG, 0, 0);

        DialogResult = true;
    }

    private void RefreshEmptyState() =>
        _emptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static QuickAccessItem Clone(QuickAccessItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Target = item.Target,
        Arguments = item.Arguments,
        WorkingDirectory = item.WorkingDirectory,
        IconMode = item.IconMode,
        IconPath = item.IconPath,
        Enabled = item.Enabled,
        Order = item.Order,
    };

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(16, 9, 16, 9),
            Cursor = Cursors.Hand,
            FontSize = 13,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = primary ? BrushFrom("#04211D") : MutedBrush,
            Background = primary ? AccentBrush : Brushes.Transparent,
            BorderBrush = primary ? AccentBrush : BorderBrush,
            BorderThickness = new Thickness(1),
        };
        return button;
    }

    private static Brush BrushFrom(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}

internal sealed class QuickAccessEditorWindow : Window
{
    private readonly TextBox _name;
    private readonly TextBox _target;
    private readonly TextBox _arguments;
    private readonly TextBox _workingDirectory;
    private readonly TextBox _iconPath;
    private readonly CheckBox _enabled;
    private readonly ComboBox _iconMode;
    private readonly Guid _id;

    public QuickAccessItem? Value { get; private set; }

    public QuickAccessEditorWindow(QuickAccessItem? source)
    {
        _id = source?.Id ?? Guid.NewGuid();
        Title = source is null ? "Agregar acceso rápido" : "Editar acceso rápido";
        Width = 650;
        Height = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = BrushFrom("#0F1116");
        Foreground = BrushFrom("#EAF0F5");

        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        root.Children.Add(new TextBlock
        {
            Text = Title,
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFrom("#EAF0F5"),
            Margin = new Thickness(0, 0, 0, 18),
        });

        var form = new StackPanel();
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        _name = AddField(form, "Nombre", source?.Name ?? string.Empty, "Ej.: Conectar VPN");

        var targetRow = AddFieldWithButton(form, "Destino", source?.Target ?? string.Empty, "Seleccionar...", SelectTarget);
        _target = targetRow;
        _arguments = AddField(form, "Argumentos (opcional)", source?.Arguments ?? string.Empty, "Ej.: --profile Producción");
        _workingDirectory = AddField(form, "Directorio de trabajo (opcional)", source?.WorkingDirectory ?? string.Empty, string.Empty);

        var modeLabel = Label("Icono");
        form.Children.Add(modeLabel);
        _iconMode = new ComboBox
        {
            ItemsSource = new[] { "Automático", "Personalizado" },
            SelectedIndex = source?.IconMode == QuickAccessIconMode.Custom ? 1 : 0,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8, 6, 8, 6),
        };
        form.Children.Add(_iconMode);

        _iconPath = AddFieldWithButton(form, "Archivo de icono", source?.IconPath ?? string.Empty, "Elegir...", SelectIcon);
        _iconPath.IsEnabled = _iconMode.SelectedIndex == 1;
        _iconMode.SelectionChanged += (_, _) => _iconPath.IsEnabled = _iconMode.SelectedIndex == 1;

        _enabled = new CheckBox
        {
            Content = "Activo",
            IsChecked = source?.Enabled ?? true,
            Foreground = BrushFrom("#EAF0F5"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        form.Children.Add(_enabled);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        var cancel = Button("Cancelar", false);
        cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) => Close();
        footer.Children.Add(cancel);

        var save = Button("Guardar", true);
        save.Click += (_, _) => Accept();
        footer.Children.Add(save);
    }

    private void Accept()
    {
        string name = _name.Text.Trim();
        string target = _target.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "Nombre y destino son obligatorios.", "Vitals", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Value = new QuickAccessItem
        {
            Id = _id,
            Name = name,
            Target = target,
            Arguments = NullIfWhiteSpace(_arguments.Text),
            WorkingDirectory = NullIfWhiteSpace(_workingDirectory.Text),
            IconMode = _iconMode.SelectedIndex == 1 ? QuickAccessIconMode.Custom : QuickAccessIconMode.Automatic,
            IconPath = _iconMode.SelectedIndex == 1 ? NullIfWhiteSpace(_iconPath.Text) : null,
            Enabled = _enabled.IsChecked == true,
        };
        DialogResult = true;
    }

    private void SelectTarget()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar programa, archivo o script",
            Filter = "Todos los archivos|*.*|Programas|*.exe|Scripts|*.bat;*.cmd;*.ps1",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _target.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(_name.Text))
                _name.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            if (string.IsNullOrWhiteSpace(_workingDirectory.Text))
                _workingDirectory.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
        }
    }

    private void SelectIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar icono",
            Filter = "Iconos e imágenes|*.ico;*.png;*.jpg;*.jpeg;*.exe|Todos los archivos|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            _iconPath.Text = dialog.FileName;
    }

    private static TextBox AddField(Panel parent, string label, string value, string tooltip)
    {
        parent.Children.Add(Label(label));
        var textBox = TextBox(value, tooltip);
        parent.Children.Add(textBox);
        return textBox;
    }

    private static TextBox AddFieldWithButton(Panel parent, string label, string value, string buttonText, Action click)
    {
        parent.Children.Add(Label(label));
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        parent.Children.Add(grid);

        var text = TextBox(value, string.Empty);
        text.Margin = new Thickness(0);
        grid.Children.Add(text);

        var button = Button(buttonText, false);
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Click += (_, _) => click();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return text;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = BrushFrom("#8A97A6"),
        FontSize = 11.5,
        Margin = new Thickness(0, 0, 0, 5),
    };

    private static TextBox TextBox(string value, string tooltip) => new()
    {
        Text = value,
        ToolTip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip,
        Background = BrushFrom("#1D212A"),
        Foreground = BrushFrom("#EAF0F5"),
        BorderBrush = BrushFrom("#2A303B"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(9, 7, 9, 7),
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static Button Button(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(16, 9, 16, 9),
        Cursor = Cursors.Hand,
        FontSize = 13,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = primary ? BrushFrom("#04211D") : BrushFrom("#8A97A6"),
        Background = primary ? BrushFrom("#2AD1BE") : Brushes.Transparent,
        BorderBrush = primary ? BrushFrom("#2AD1BE") : BrushFrom("#2A303B"),
        BorderThickness = new Thickness(1),
    };

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Brush BrushFrom(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
