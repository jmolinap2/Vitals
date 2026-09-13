using System.Collections.ObjectModel;
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
    private readonly CheckBox _enabled;
    private readonly CheckBox _showLabels;
    private readonly CheckBox _collapseOnLaunch;
    private readonly ComboBox _columns;
    private readonly ComboBox _iconSize;
    private readonly ComboBox _animation;
    private readonly ComboBox _direction;
    private readonly ComboBox _layout;

    private static readonly Brush Bg = BrushFrom("#0F1116");
    private static readonly Brush Card = BrushFrom("#171A21");
    private static readonly Brush Row = BrushFrom("#1D212A");
    private static readonly Brush Text = BrushFrom("#EAF0F5");
    private static readonly Brush Muted = BrushFrom("#8A97A6");
    private static readonly Brush Accent = BrushFrom("#2AD1BE");
    private static readonly Brush Line = BrushFrom("#2A303B");

    public QuickAccessManagerWindow()
    {
        Title = "Accesos rápidos de Vitals";
        Width = 900;
        Height = 690;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;
        Foreground = Text;

        _items = new ObservableCollection<QuickAccessItem>(
            QuickAccessStore.Load().OrderBy(x => x.Order).Select(Clone));
        var launcher = QuickLauncherStore.Load();

        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var heading = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Launcher de la cápsula", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Text },
                new TextBlock
                {
                    Text = "La cápsula se despliega y muestra tus accesos directos, programas, archivos y carpetas.",
                    FontSize = 12.5,
                    Foreground = Muted,
                    Margin = new Thickness(0, 4, 0, 0),
                },
            },
        });

        var add = Button("+  Agregar acceso", true);
        add.Click += (_, _) => AddItem();
        Grid.SetColumn(add, 1);
        heading.Children.Add(add);
        root.Children.Add(heading);

        var options = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 14),
        };
        Grid.SetRow(options, 1);
        root.Children.Add(options);

        var optionGrid = new Grid();
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        options.Child = optionGrid;

        var left = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
        optionGrid.Children.Add(left);
        _enabled = Check("Habilitar launcher desplegable", launcher.Enabled);
        _showLabels = Check("Mostrar nombres bajo los iconos", launcher.ShowLabels);
        _collapseOnLaunch = Check("Plegar después de abrir un acceso", launcher.CollapseOnLaunch);
        left.Children.Add(_enabled);
        left.Children.Add(_showLabels);
        left.Children.Add(_collapseOnLaunch);

        var right = new Grid();
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 1);
        optionGrid.Children.Add(right);

        _columns = Combo(new object[] { 2, 3, 4, 5, 6 }, Math.Clamp(launcher.Columns, 2, 6));
        _iconSize = Combo(new object[] { 24, 28, 32, 36, 40, 44, 48 }, Nearest(new[] { 24, 28, 32, 36, 40, 44, 48 }, launcher.IconSize));
        _animation = Combo(new object[] { 100, 120, 160, 200, 250, 300 }, Nearest(new[] { 100, 120, 160, 200, 250, 300 }, launcher.AnimationMs));
        _direction = Combo(new object[] { "Automático", "Abajo", "Arriba", "Derecha", "Izquierda" }, launcher.Direction switch
        {
            QuickLauncherDirection.Down => "Abajo",
            QuickLauncherDirection.Up => "Arriba",
            QuickLauncherDirection.Right => "Derecha",
            QuickLauncherDirection.Left => "Izquierda",
            _ => "Automático",
        });
        _layout = Combo(new object[] { "Cuadrícula", "Fila", "Columna" }, launcher.Layout switch
        {
            QuickLauncherLayout.Row => "Fila",
            QuickLauncherLayout.Column => "Columna",
            _ => "Cuadrícula",
        });
        AddLabeled(right, "Distribución", _layout, 0, 0);
        AddLabeled(right, "Columnas (cuadrícula)", _columns, 0, 1);
        AddLabeled(right, "Tamaño del icono", _iconSize, 1, 0);
        AddLabeled(right, "Dirección de despliegue", _direction, 1, 1);
        AddLabeled(right, "Animación (ms)", _animation, 2, 0);

        var listCard = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
        };
        Grid.SetRow(listCard, 2);
        root.Children.Add(listCard);

        var listGrid = new Grid();
        listCard.Child = listGrid;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Text,
            ItemsSource = _items,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = BuildItemTemplate(),
        };
        _list.MouseDoubleClick += (_, _) => EditSelected();
        listGrid.Children.Add(_list);

        _emptyState = new TextBlock
        {
            Text = "Todavía no hay accesos.\nPuedes agregar incluso accesos directos de Windows (.lnk).",
            Foreground = Muted,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        listGrid.Children.Add(_emptyState);
        RefreshEmptyState();

        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        var itemActions = new StackPanel { Orientation = Orientation.Horizontal };
        footer.Children.Add(itemActions);
        AddAction(itemActions, "Editar", (_, _) => EditSelected());
        AddAction(itemActions, "Eliminar", (_, _) => RemoveSelected());
        AddAction(itemActions, "↑", (_, _) => MoveSelected(-1));
        AddAction(itemActions, "↓", (_, _) => MoveSelected(1));

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var cancel = Button("Cancelar", false);
        cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) => Close();
        actions.Children.Add(cancel);

        var save = Button("Guardar cambios", true);
        save.Click += (_, _) => SaveAndClose();
        actions.Children.Add(save);
    }

    private static DataTemplate BuildItemTemplate()
    {
        var template = new DataTemplate(typeof(QuickAccessItem));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Row);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 9, 12, 9));
        border.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 7));

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(DockPanel.LastChildFillProperty, true);

        var active = new FrameworkElementFactory(typeof(CheckBox));
        active.SetBinding(CheckBox.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(QuickAccessItem.Enabled)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        active.SetValue(ContentControl.ContentProperty, "Activo");
        active.SetValue(Control.ForegroundProperty, Muted);
        active.SetValue(DockPanel.DockProperty, Dock.Right);
        active.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        active.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0));
        dock.AppendChild(active);

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickAccessItem.Name)));
        name.SetValue(TextBlock.ForegroundProperty, Text);
        name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        name.SetValue(TextBlock.FontSizeProperty, 13.5d);
        panel.AppendChild(name);

        var target = new FrameworkElementFactory(typeof(TextBlock));
        target.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(QuickAccessItem.Target)));
        target.SetValue(TextBlock.ForegroundProperty, Muted);
        target.SetValue(TextBlock.FontSizeProperty, 11d);
        target.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        target.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 0, 0));
        panel.AppendChild(target);
        dock.AppendChild(panel);

        border.AppendChild(dock);
        template.VisualTree = border;
        return template;
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

    private void RemoveSelected()
    {
        if (_list.SelectedItem is not QuickAccessItem selected) return;
        if (MessageBox.Show(this, $"¿Eliminar '{selected.Name}'?", "Vitals", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _items.Remove(selected);
        RefreshEmptyState();
    }

    private void MoveSelected(int delta)
    {
        if (_list.SelectedItem is not QuickAccessItem selected) return;
        int from = _items.IndexOf(selected);
        int to = from + delta;
        if (to < 0 || to >= _items.Count) return;
        _items.Move(from, to);
        _list.SelectedIndex = to;
    }

    private void SaveAndClose()
    {
        QuickAccessStore.Save(_items.Select((x, i) =>
        {
            var copy = Clone(x);
            copy.Order = i;
            return copy;
        }));

        QuickLauncherStore.Save(new QuickLauncherConfig
        {
            Enabled = _enabled.IsChecked == true,
            Columns = Convert.ToInt32(_columns.SelectedItem),
            IconSize = Convert.ToInt32(_iconSize.SelectedItem),
            AnimationMs = Convert.ToInt32(_animation.SelectedItem),
            ShowLabels = _showLabels.IsChecked == true,
            CollapseOnLaunch = _collapseOnLaunch.IsChecked == true,
            Direction = (_direction.SelectedItem?.ToString()) switch
            {
                "Abajo" => QuickLauncherDirection.Down,
                "Arriba" => QuickLauncherDirection.Up,
                "Derecha" => QuickLauncherDirection.Right,
                "Izquierda" => QuickLauncherDirection.Left,
                _ => QuickLauncherDirection.Auto,
            },
            Layout = (_layout.SelectedItem?.ToString()) switch
            {
                "Fila" => QuickLauncherLayout.Row,
                "Columna" => QuickLauncherLayout.Column,
                _ => QuickLauncherLayout.Grid,
            },
        });

        nint hwnd = NativeInterop.FindWindow("VitalsPillWindow", null);
        if (hwnd != 0) NativeInterop.PostMessage(hwnd, NativeInterop.WM_RELOAD_CONFIG, 0, 0);
        DialogResult = true;
    }

    private void RefreshEmptyState() =>
        _emptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private static QuickAccessItem Clone(QuickAccessItem x) => new()
    {
        Id = x.Id,
        Name = x.Name,
        Target = x.Target,
        Arguments = x.Arguments,
        WorkingDirectory = x.WorkingDirectory,
        IconMode = x.IconMode,
        IconPath = x.IconPath,
        Enabled = x.Enabled,
        Order = x.Order,
    };

    private static int Nearest(int[] values, int value) =>
        values.OrderBy(x => Math.Abs(x - value)).First();

    private static void AddAction(Panel parent, string text, RoutedEventHandler handler)
    {
        var button = Button(text, false);
        button.Margin = new Thickness(0, 0, 8, 0);
        button.Click += handler;
        parent.Children.Add(button);
    }

    private static CheckBox Check(string text, bool value) => new()
    {
        Content = text,
        IsChecked = value,
        Foreground = Text,
        Margin = new Thickness(0, 0, 0, 11),
        FontSize = 12.5,
    };

    private static ComboBox Combo(IEnumerable<object> items, object selected) => new()
    {
        ItemsSource = items,
        SelectedItem = selected,
        Padding = new Thickness(7, 5, 7, 5),
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static void AddLabeled(Grid grid, string label, Control control, int row, int column)
    {
        var stack = new StackPanel
        {
            Margin = new Thickness(column == 0 ? 0 : 8, 0, column == 0 ? 8 : 0, 0),
        };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = 11.5,
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(control);
        Grid.SetRow(stack, row);
        Grid.SetColumn(stack, column);
        grid.Children.Add(stack);
    }

    private static Button Button(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(16, 9, 16, 9),
        Cursor = Cursors.Hand,
        FontSize = 13,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = primary ? BrushFrom("#04211D") : Muted,
        Background = primary ? Accent : Brushes.Transparent,
        BorderBrush = primary ? Accent : Line,
        BorderThickness = new Thickness(1),
    };

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
    private readonly Expander _advanced;
    private readonly FrameworkElement _customIconSection;
    private readonly Guid _id;

    private bool _updatingName;
    private bool _nameIsAutomatic;

    public QuickAccessItem? Value { get; private set; }

    public QuickAccessEditorWindow(QuickAccessItem? source)
    {
        _id = source?.Id ?? Guid.NewGuid();
        Title = source is null ? "Agregar acceso" : "Editar acceso";
        Width = 680;
        Height = 535;
        MinHeight = 535;
        MaxHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background = BrushFrom("#0F1116");
        Foreground = BrushFrom("#EAF0F5");

        string sourceTarget = source?.Target ?? string.Empty;
        string suggestedSourceName = SuggestedName(sourceTarget);
        _nameIsAutomatic = source is null
            || string.IsNullOrWhiteSpace(source.Name)
            || string.Equals(source.Name, suggestedSourceName, StringComparison.OrdinalIgnoreCase);

        var root = new Grid { Margin = new Thickness(26, 22, 26, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        root.Children.Add(new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 18),
            Children =
            {
                new TextBlock
                {
                    Text = Title,
                    FontSize = 21,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = BrushFrom("#EAF0F5"),
                },
                new TextBlock
                {
                    Text = "Elige qué quieres abrir. Vitals completa el nombre y toma el icono automáticamente.",
                    FontSize = 12,
                    Foreground = BrushFrom("#8A97A6"),
                    Margin = new Thickness(0, 5, 0, 0),
                },
            },
        });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var form = new StackPanel();
        scroll.Content = form;

        form.Children.Add(Label("Nombre"));
        _name = TextBox(source?.Name ?? string.Empty, "Puedes cambiarlo aunque Vitals lo complete automáticamente.");
        _name.Margin = new Thickness(0, 0, 0, 4);
        form.Children.Add(_name);
        form.Children.Add(new TextBlock
        {
            Text = "Se completa automáticamente al elegir el acceso; puedes editarlo después.",
            Foreground = BrushFrom("#6F7C8B"),
            FontSize = 10.5,
            Margin = new Thickness(1, 0, 0, 12),
        });

        form.Children.Add(Label("Acceso"));
        _target = AddAccessField(form, sourceTarget);

        form.Children.Add(Label("Icono"));
        _iconMode = new ComboBox
        {
            ItemsSource = new[] { "Automático", "Personalizado" },
            SelectedIndex = source?.IconMode == QuickAccessIconMode.Custom ? 1 : 0,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(8, 6, 8, 6),
        };
        form.Children.Add(_iconMode);

        _enabled = new CheckBox
        {
            Content = "Activo",
            IsChecked = source?.Enabled ?? true,
            Foreground = BrushFrom("#EAF0F5"),
            Margin = new Thickness(0, 2, 0, 12),
        };
        form.Children.Add(_enabled);

        var advancedContent = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _arguments = AddField(advancedContent, "Argumentos", source?.Arguments ?? string.Empty,
            "Solo si el programa necesita parámetros al iniciarse.");
        _workingDirectory = AddField(advancedContent, "Directorio de trabajo", source?.WorkingDirectory ?? string.Empty,
            "Normalmente no necesitas cambiarlo.");

        var customIcon = new StackPanel();
        _customIconSection = customIcon;
        _iconPath = AddFieldWithButton(customIcon, "Archivo de icono personalizado",
            source?.IconMode == QuickAccessIconMode.Custom ? source.IconPath ?? string.Empty : string.Empty,
            "Elegir...", SelectIcon);
        advancedContent.Children.Add(customIcon);

        _advanced = new Expander
        {
            Header = "Opciones avanzadas",
            Foreground = BrushFrom("#8A97A6"),
            Content = advancedContent,
            IsExpanded = source?.IconMode == QuickAccessIconMode.Custom
                || !string.IsNullOrWhiteSpace(source?.Arguments)
                || !string.IsNullOrWhiteSpace(source?.WorkingDirectory),
            Margin = new Thickness(0, 2, 0, 0),
        };
        form.Children.Add(_advanced);

        _name.TextChanged += (_, _) =>
        {
            if (!_updatingName) _nameIsAutomatic = false;
        };
        _target.TextChanged += (_, _) =>
        {
            if (_nameIsAutomatic) ApplyAutomaticName();
        };
        _iconMode.SelectionChanged += (_, _) => UpdateIconModeUi();
        UpdateIconModeUi();

        if (_nameIsAutomatic && !string.IsNullOrWhiteSpace(sourceTarget))
            ApplyAutomaticName();

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

    private TextBox AddAccessField(Panel parent, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        parent.Children.Add(grid);

        var text = TextBox(value, "Ruta de un .lnk, programa, archivo, carpeta o URL.");
        text.Margin = new Thickness(0);
        grid.Children.Add(text);

        var file = Button("Archivo...", false);
        file.Margin = new Thickness(8, 0, 0, 0);
        file.Click += (_, _) => SelectFile();
        Grid.SetColumn(file, 1);
        grid.Children.Add(file);

        var folder = Button("Carpeta...", false);
        folder.Margin = new Thickness(8, 0, 0, 0);
        folder.Click += (_, _) => SelectFolder();
        Grid.SetColumn(folder, 2);
        grid.Children.Add(folder);

        return text;
    }

    private void Accept()
    {
        string target = _target.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "Selecciona o escribe un acceso.", "Vitals",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string name = _name.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            _nameIsAutomatic = true;
            ApplyAutomaticName();
            name = _name.Text.Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "No pude obtener un nombre. Escribe uno para continuar.", "Vitals",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool customIcon = _iconMode.SelectedIndex == 1;
        Value = new QuickAccessItem
        {
            Id = _id,
            Name = name,
            Target = target,
            Arguments = Null(_arguments.Text),
            WorkingDirectory = Null(_workingDirectory.Text),
            IconMode = customIcon ? QuickAccessIconMode.Custom : QuickAccessIconMode.Automatic,
            IconPath = customIcon ? Null(_iconPath.Text) : null,
            Enabled = _enabled.IsChecked == true,
        };
        DialogResult = true;
    }

    private void SelectFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar acceso, programa, archivo o script",
            Filter = "Accesos directos y programas|*.lnk;*.url;*.exe;*.bat;*.cmd;*.ps1|Todos los archivos|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            UseSelectedAccess(dialog.FileName);
    }

    private void SelectFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Seleccionar carpeta",
        };
        if (dialog.ShowDialog(this) == true)
            UseSelectedAccess(dialog.FolderName);
    }

    private void UseSelectedAccess(string path)
    {
        bool shouldGenerateName = _nameIsAutomatic || string.IsNullOrWhiteSpace(_name.Text);
        _target.Text = path;

        if (shouldGenerateName)
        {
            _nameIsAutomatic = true;
            ApplyAutomaticName();
        }

        if (string.IsNullOrWhiteSpace(_workingDirectory.Text))
        {
            _workingDirectory.Text = Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(path) ?? string.Empty;
        }
    }

    private void ApplyAutomaticName()
    {
        if (!_nameIsAutomatic) return;
        string suggested = SuggestedName(_target.Text);
        if (string.IsNullOrWhiteSpace(suggested)) return;

        _updatingName = true;
        try
        {
            _name.Text = suggested;
        }
        finally
        {
            _updatingName = false;
        }
    }

    private static string SuggestedName(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return string.Empty;
        string value = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            string leaf = Path.GetFileNameWithoutExtension(uri.LocalPath.TrimEnd('/'));
            return string.IsNullOrWhiteSpace(leaf) ? uri.Host : Uri.UnescapeDataString(leaf);
        }

        string trimmed = value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName)) return trimmed;
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private void UpdateIconModeUi()
    {
        bool custom = _iconMode.SelectedIndex == 1;
        _customIconSection.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        _iconPath.IsEnabled = custom;

        if (custom)
        {
            _advanced.IsExpanded = true;
        }
        else
        {
            _iconPath.Text = string.Empty;
        }
    }

    private void SelectIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar icono",
            Filter = "Iconos y ejecutables|*.ico;*.exe;*.dll|Todos los archivos|*.*",
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

    private static string? Null(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Brush BrushFrom(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
}
