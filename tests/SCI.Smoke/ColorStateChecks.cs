using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using McDownload.View;
using McDownload.ViewModel;

internal static class ColorStateChecks
{
    // Inject read-only interaction flags to test overlapping triggers without moving the user's mouse.
    private static void State(DependencyObject control, DependencyProperty property, bool value)
    {
        for (Type? type = control.GetType(); type is not null; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (field.FieldType == typeof(DependencyPropertyKey) && field.GetValue(null) is DependencyPropertyKey key && key.DependencyProperty == property)
                { control.SetValue(key, value); return; }
        throw new InvalidOperationException("Missing state key: " + property.Name);
    }

    private static bool Is(Brush brush, string color) => brush is SolidColorBrush solid && solid.Color == (Color)ColorConverter.ConvertFromString(color);
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Find<T>(child)) yield return nested;
        }
    }

    public static async Task RunAsync(MainWindow owner, MainWindowViewModel model, Action<bool, string> check,
        Action<string, Window?> capture)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        for (int i = 0; i < 5; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        string[] columns = ["默认", "悬停", "选中 / 焦点", "按下 / 拖动", "禁用"];
        for (int i = 0; i < columns.Length; i++)
        {
            var label = new TextBlock { Text = columns[i], HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(label, i + 1); grid.Children.Add(label);
        }
        var window = new Window { Owner = owner, Title = "控件颜色状态", Width = 1040, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new Border { Padding = new Thickness(24), Child = grid }, ShowInTaskbar = false };
        Control[] Row(string title, Func<Control> create)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row); grid.Children.Add(label);
            return Enumerable.Range(0, 5).Select(column =>
            {
                var control = create();
                control.Margin = new Thickness(5, 4, 5, 4); control.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetRow(control, row); Grid.SetColumn(control, column + 1); grid.Children.Add(control);
                return control;
            }).ToArray();
        }
        var ordinary = Row("普通按钮", () => new Button { Content = "浏览" });
        var primary = Row("主要按钮", () => new Button { Content = "下载并安装", Style = (Style)owner.FindResource("Primary") });
        var danger = Row("危险按钮", () => new Button { Content = "删除日志", Style = (Style)owner.FindResource("Danger") });
        var items = Row("列表选项", () => new ListBoxItem { Content = "Minecraft 1.20.1" });
        var choices = Row("下拉选项", () => new ComboBoxItem { Content = "正式版" });
        var toggles = Row("切换按钮", () => new ToggleButton { Content = "选择" });
        var inputs = Row("输入框", () => new TextBox { Text = "Minecraft" });
        var combos = Row("下拉框", () => new ComboBox { ItemsSource = new[] { "正式版", "测试版" }, SelectedIndex = 0, MinWidth = 0 });
        var expanders = Row("折叠标题", () => new ToggleButton { Content = "详细进度", Style = (Style)owner.FindResource("ExpanderToggle") });
        var thumbs = Row("滚动滑块", () => new Thumb { Height = 12, Style = (Style)owner.FindResource("ScrollThumb") });
        try
        {
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            foreach (var row in new[] { ordinary, primary, danger, items, toggles, inputs, combos, expanders, thumbs })
                State(row[1], UIElement.IsMouseOverProperty, true);
            State(choices[1], ComboBoxItem.IsHighlightedProperty, true);
            foreach (var row in new[] { ordinary, primary, danger, toggles, expanders })
            {
                State(row[2], UIElement.IsKeyboardFocusedProperty, true);
                State(row[3], UIElement.IsMouseOverProperty, true);
                State(row[3], ButtonBase.IsPressedProperty, true);
            }
            foreach (var row in new[] { items, choices })
            {
                ((ListBoxItem)row[2]).IsSelected = true;
                ((ListBoxItem)row[3]).IsSelected = true;
                State(row[3], row == items ? UIElement.IsMouseOverProperty : ComboBoxItem.IsHighlightedProperty, true);
            }
            ((ToggleButton)toggles[2]).IsChecked = true;
            ((ToggleButton)expanders[2]).IsChecked = true;
            State(inputs[2], UIElement.IsKeyboardFocusWithinProperty, true);
            State(inputs[3], UIElement.IsKeyboardFocusWithinProperty, true);
            State(combos[2], UIElement.IsKeyboardFocusWithinProperty, true);
            Validation.MarkInvalid(((TextBox)inputs[3]).GetBindingExpression(TextBox.TextProperty) ?? BindText((TextBox)inputs[3]),
                new ValidationError(new ExceptionValidationRule(), new object(), "无效值", null));
            State(thumbs[3], Thumb.IsDraggingProperty, true);
            foreach (var row in new[] { ordinary, primary, danger, items, choices, toggles, inputs, combos, expanders, thumbs })
            {
                State(row[4], UIElement.IsMouseOverProperty, true);
                if (row[4] is ButtonBase) State(row[4], ButtonBase.IsPressedProperty, true);
                if (row[4] is ListBoxItem item) item.IsSelected = true;
                if (row[4] is ToggleButton toggle) toggle.IsChecked = true;
                row[4].IsEnabled = false;
            }
            window.UpdateLayout();
            string[][] expected = [["#FFFFFF", "#F0F6FD", "#FFFFFF", "#E2EEFB"],
                ["#1976D2", "#1565B0", "#1976D2", "#0D559F"], ["#BC3030", "#A82A2A", "#BC3030", "#922424"]];
            int type = 0;
            foreach (var row in new[] { ordinary, primary, danger })
            {
                for (int i = 0; i < 4; i++)
                    check(Is(row[i].Background, expected[type][i]) && Is(Find<Border>(row[i]).First().Background, expected[type][i]),
                        $"button palette {type} state {i} reaches the rendered border");
                check(Is(row[2].BorderBrush, type == 2 ? "#7E2020" : "#0D47A1"), "button keyboard focus has a distinct border");
                type++;
            }
            foreach (var row in new[] { items, choices })
            {
                check(Is(row[1].Background, "#F0F6FD") && Is(row[2].Background, "#1976D2") && Is(row[2].Foreground, "#FFFFFF") &&
                    Is(row[3].Background, "#1565B0"), "list and dropdown hover, selected and selected-hover remain distinct");
            }
            check(Is(toggles[2].Background, "#1976D2") && Is(toggles[2].Foreground, "#FFFFFF") && Is(toggles[3].Background, "#0D559F"),
                "toggle checked and pressed states use the accent palette");
            check(Is(inputs[2].BorderBrush, "#1976D2") && Is(inputs[3].BorderBrush, "#BC3030") &&
                Is(((TextBox)inputs[2]).SelectionBrush, "#1976D2") && Is(((TextBox)inputs[2]).SelectionTextBrush, "#FFFFFF"),
                "input validation stays red while focused, and text selection uses blue and white");
            foreach (var row in new[] { ordinary, primary, danger, items, choices, toggles, inputs })
                check(Is(row[4].Background, "#EDF1F5") && Is(row[4].Foreground, "#748397"),
                    "disabled palette overrides hover, pressed or selected states");
            var comboToggle = (ToggleButton)combos[4].Template.FindName("DropDownToggle", combos[4]);
            check(Is(comboToggle.Background, "#EDF1F5") && Is(comboToggle.Foreground, "#748397") && Is(combos[4].Foreground, "#748397"),
                "disabled dropdown surface, arrow and text use the disabled palette");
            check(Is(thumbs[0].Background, "#BACADD") && Is(thumbs[1].Background, "#8BA6C2") && Is(thumbs[3].Background, "#6D91B5") &&
                Is(thumbs[4].Background, "#D3DDE9"), "scroll thumb default, hover, dragging and disabled colors are distinct");
            check(Is(expanders[1].Background, "#F0F6FD") && Is(expanders[2].Background, "#E2EEFB") &&
                Is(expanders[3].Background, "#D2E5F9") && expanders[4].Opacity == .45,
                "expander hover, expanded, pressed and disabled colors are defined");
            foreach (string style in new[] { "Secondary", "MutedButton" })
            {
                var button = new Button { Style = (Style)owner.FindResource(style) };
                State(button, UIElement.IsMouseOverProperty, true);
                check(Is(button.Background, "#F0F6FD"), "secondary action retains hover feedback: " + style);
                button.IsEnabled = false;
                check(Is(button.Background, "#EDF1F5") && Is(button.Foreground, "#748397"), "secondary action retains disabled feedback: " + style);
            }
            var openCombo = (ComboBox)combos[3];
            var popup = (Popup)openCombo.Template.FindName("PART_Popup", openCombo);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            popup.Closed += (_, _) => closed.TrySetResult();
            openCombo.IsDropDownOpen = true;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var openToggle = (ToggleButton)openCombo.Template.FindName("DropDownToggle", openCombo);
            check(Is(openToggle.Background, "#F0F6FD") && Is(openToggle.BorderBrush, "#1976D2") &&
                Is(openToggle.Foreground, "#1976D2"), "opened dropdown preserves its blue arrow, border and light surface");
            openCombo.IsDropDownOpen = false;
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            capture("SCI-color-states", window);
        }
        finally { window.Close(); }

        string previous = model.CurrentPage;
        try
        {
            var navigation = Find<Button>(owner).Where(x => x.Style == owner.FindResource("NavigationButton")).ToArray();
            foreach (string page in new[] { "Instances", "Download", "Settings", "Loaders", "Tasks", "About", "Licenses", "ProjectLicense" })
            {
                model.CurrentPage = page;
                await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                owner.UpdateLayout();
                var selected = navigation.Where(x => x.Tag is true).ToArray();
                string expectedPage = page == "Loaders" ? "Instances" : page is "Licenses" or "ProjectLicense" ? "About" : page;
                check(selected.Length == 1 && Equals(selected[0].CommandParameter, expectedPage) &&
                    Is(selected[0].Background, "#1976D2") && Is(selected[0].Foreground, "#FFFFFF"), "exactly one navigation entry identifies page " + page);
                var current = selected[0];
                try
                {
                    State(current, UIElement.IsMouseOverProperty, true);
                    check(Is(current.Background, "#1565B0"), "active navigation retains hover feedback");
                    State(current, ButtonBase.IsPressedProperty, true);
                    check(Is(current.Background, "#0D559F"), "active navigation retains pressed feedback");
                    current.IsEnabled = false;
                    check(Is(current.Background, "#EDF1F5") && Is(current.Foreground, "#748397"), "disabled navigation overrides the active state");
                }
                finally
                {
                    State(current, ButtonBase.IsPressedProperty, false);
                    State(current, UIElement.IsMouseOverProperty, false);
                    current.ClearValue(UIElement.IsEnabledProperty);
                }
            }
            model.CurrentPage = "Instances";
            await owner.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            owner.UpdateLayout();
            var selectedItem = Find<ListBoxItem>(Find<InstanceListView>(owner).Single()).Single(x => x.IsSelected);
            check(Is(selectedItem.Background, "#1976D2") && Find<TextBlock>(selectedItem).All(x => Is(x.Foreground, "#FFFFFF")),
                "all selected instance labels inherit readable white text");
            capture("SCI-selected-instance", owner);
        }
        finally { model.CurrentPage = previous; }
    }

    private static System.Windows.Data.BindingExpression BindText(TextBox input) =>
        (System.Windows.Data.BindingExpression)input.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(TextBox.Text)) { Source = new TextBox { Text = "无效值" } });
}
