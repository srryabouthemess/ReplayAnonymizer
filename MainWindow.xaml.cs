using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ReplayAnonymizer;

public partial class MainWindow : Window
{
    public ObservableCollection<ReplayItem> Replays { get; } = [];
    private Point dragStartPoint;
    private ReplayItem? draggedItem;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
        }
        else
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }
        base.OnStateChanged(e);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Replays do osu! (*.osr)|*.osr|Todos os arquivos (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Escolha uma pasta com replays", Multiselect = false };
        if (dialog.ShowDialog(this) == true) AddPaths(Directory.EnumerateFiles(dialog.FolderName, "*.osr", SearchOption.TopDirectoryOnly));
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths.SelectMany(ExpandPath));
    }

    private static IEnumerable<string> ExpandPath(string path) => Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*.osr", SearchOption.TopDirectoryOnly)
        : File.Exists(path) ? [path] : [];

    private void AddPaths(IEnumerable<string> paths)
    {
        var existing = Replays.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        var errors = new List<string>();
        foreach (string path in paths.Where(path => path.EndsWith(".osr", StringComparison.OrdinalIgnoreCase)))
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            if (!existing.Add(fullPath)) continue;
            try
            {
                Replays.Add(new ReplayItem { Path = fullPath, OriginalName = OsrAnonymizer.ReadPlayerName(fullPath) });
                added++;
            }
            catch (Exception ex) { errors.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}"); }
        }
        GenerateAliases();
        UpdateOrderAndCount();
        StatusText.Text = added > 0 ? $"{added} replay(s) adicionado(s)." : "Nenhum replay novo foi adicionado.";
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n", errors), "Alguns arquivos não puderam ser lidos", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void RemoveSelected()
    {
        foreach (ReplayItem item in ReplayGrid.SelectedItems.Cast<ReplayItem>().ToList()) Replays.Remove(item);
        UpdateOrderAndCount();
        StatusText.Text = $"{Replays.Count} replay(s) na lista.";
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void ReplayGrid_KeyDown(object sender, KeyEventArgs e)
    {
        Key pressedKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (pressedKey == Key.Delete && Keyboard.FocusedElement is not TextBox)
        {
            RemoveSelected();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0 || (pressedKey != Key.Up && pressedKey != Key.Down)) return;
        MoveSelected(pressedKey == Key.Up ? -1 : 1);
        e.Handled = true;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        ReplayGrid.SelectAll();
        ReplayGrid.Focus();
        StatusText.Text = $"{Replays.Count} replay(s) selecionado(s).";
    }

    private void ReplayGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        dragStartPoint = e.GetPosition(ReplayGrid);
        draggedItem = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item as ReplayItem;
    }

    private void ReplayGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DataGridRow? row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null) return;
        if (!row.IsSelected)
        {
            ReplayGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
        row.Focus();
    }

    private void ContextRemove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

    private void ReplayGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggedItem is null) return;
        Point current = e.GetPosition(ReplayGrid);
        if (Math.Abs(current.X - dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var moving = ReplayGrid.SelectedItems.Cast<ReplayItem>().ToList();
        if (!moving.Contains(draggedItem)) moving = [draggedItem];
        DragDrop.DoDragDrop(ReplayGrid, moving, DragDropEffects.Move);
        draggedItem = null;
    }

    private void ReplayGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        else if (e.Data.GetDataPresent(typeof(List<ReplayItem>))) e.Effects = DragDropEffects.Move;
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ReplayGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            AddPaths(paths.SelectMany(ExpandPath));
            e.Handled = true;
            return;
        }
        if (e.Data.GetData(typeof(List<ReplayItem>)) is not List<ReplayItem> moving || moving.Count == 0) return;
        DataGridRow? targetRow = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        ReplayItem? target = targetRow?.Item as ReplayItem;
        if (target is not null && moving.Contains(target)) return;

        int targetIndex = target is null ? Replays.Count : Replays.IndexOf(target);
        if (targetRow is not null && e.GetPosition(targetRow).Y > targetRow.ActualHeight / 2) targetIndex++;
        targetIndex -= moving.Count(item => Replays.IndexOf(item) < targetIndex);
        moving = moving.OrderBy(item => item.Order).ToList();
        foreach (ReplayItem item in moving) Replays.Remove(item);
        targetIndex = Math.Clamp(targetIndex, 0, Replays.Count);
        foreach (ReplayItem item in moving) Replays.Insert(targetIndex++, item);

        UpdateOrderAndCount();
        ReplayGrid.SelectedItems.Clear();
        foreach (ReplayItem item in moving) ReplayGrid.SelectedItems.Add(item);
        StatusText.Text = $"{moving.Count} replay(s) reposicionado(s).";
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void MoveSelected(int direction)
    {
        var selected = ReplayGrid.SelectedItems.Cast<ReplayItem>().ToHashSet();
        if (selected.Count == 0) return;
        if (direction < 0)
        {
            for (int i = 1; i < Replays.Count; i++)
                if (selected.Contains(Replays[i]) && !selected.Contains(Replays[i - 1])) Replays.Move(i, i - 1);
        }
        else
        {
            for (int i = Replays.Count - 2; i >= 0; i--)
                if (selected.Contains(Replays[i]) && !selected.Contains(Replays[i + 1])) Replays.Move(i, i + 1);
        }
        UpdateOrderAndCount();
        ReplayGrid.SelectedItems.Clear();
        foreach (ReplayItem item in selected) ReplayGrid.SelectedItems.Add(item);
        StatusText.Text = $"{selected.Count} replay(s) movido(s).";
    }

    private void Randomize_Click(object sender, RoutedEventArgs e) => GenerateAliases();
    private void GenerateAliases()
    {
        var byPlayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ReplayItem item in Replays)
        {
            if (SamePlayerAliasCheck?.IsChecked == true && byPlayer.TryGetValue(item.OriginalName, out string? existing))
            {
                item.AnonymousName = existing;
                continue;
            }
            string alias;
            do { alias = "Player-" + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6); } while (!used.Add(alias));
            item.AnonymousName = alias;
            if (SamePlayerAliasCheck?.IsChecked == true) byPlayer[item.OriginalName] = alias;
        }
        if (StatusText is not null && Replays.Count > 0) StatusText.Text = "Novos nomes gerados.";
    }

    private void ApplyAliases_Click(object sender, RoutedEventArgs e)
    {
        string[] aliases = ManualAliasText.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (aliases.Length == 0) { MessageBox.Show(this, "Digite pelo menos um nome.", "Campo vazio", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var selected = ReplayGrid.SelectedItems.Cast<ReplayItem>().OrderBy(item => item.Order).ToList();
        if (selected.Count == 0) { MessageBox.Show(this, "Selecione pelo menos um replay.", "Nenhuma seleção", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        int count = RepeatAliasesCheck.IsChecked == true ? selected.Count : Math.Min(selected.Count, aliases.Length);
        for (int i = 0; i < count; i++) selected[i].AnonymousName = aliases[i % aliases.Length];
        StatusText.Text = $"{aliases.Length} nome(s) distribuído(s) entre {count} replay(s).";
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Escolha onde salvar as cópias", Multiselect = false };
        if (dialog.ShowDialog(this) == true) OutputFolderText.Text = dialog.FolderName;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        ReplayGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        if (Replays.Count == 0) { MessageBox.Show(this, "Adicione pelo menos um replay.", "Nada para processar", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (string.IsNullOrWhiteSpace(OutputFolderText.Text)) { MessageBox.Show(this, "Escolha uma pasta de saída.", "Pasta necessária", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (Replays.Any(item => string.IsNullOrWhiteSpace(item.AnonymousName))) { MessageBox.Show(this, "Todos os replays precisam ter um nome editado.", "Nome vazio", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        ExportButton.IsEnabled = false;
        ExportProgress.Visibility = Visibility.Visible;
        ExportProgress.Minimum = 0; ExportProgress.Maximum = Replays.Count; ExportProgress.Value = 0;
        var snapshot = Replays.ToList();
        string folder = OutputFolderText.Text;
        int namingMode = OutputNamingCombo.SelectedIndex;
        int completed = 0;
        var failures = new List<string>();
        await Task.Run(() =>
        {
            for (int i = 0; i < snapshot.Count; i++)
            {
                ReplayItem item = snapshot[i];
                try
                {
                    string safeAlias = string.Concat(item.AnonymousName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    string destination = UniquePath(folder, BuildOutputFileName(item, safeAlias, i + 1, namingMode));
                    OsrAnonymizer.WriteAnonymizedCopy(item.Path, destination, item.AnonymousName);
                    completed++;
                }
                catch (Exception ex) { failures.Add($"{item.FileName}: {ex.Message}"); }
                Dispatcher.Invoke(() => ExportProgress.Value = i + 1);
            }
        });
        ExportButton.IsEnabled = true;
        ExportProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Concluído: {completed} cópia(s) criada(s).";
        string message = $"{completed} replay(s) editado(s) com sucesso.";
        if (failures.Count > 0) message += $"\n\nFalhas:\n{string.Join("\n", failures)}";
        MessageBox.Show(this, message, "Processamento concluído", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static string BuildOutputFileName(ReplayItem item, string alias, int number, int mode)
    {
        string original = item.FileName;
        string sequence = number.ToString("D3");
        if (mode == 2) return $"{sequence} - {original}";
        if (mode == 1) return $"{sequence} - {alias}.osr";
        string stem = System.IO.Path.GetFileNameWithoutExtension(original);
        string remainder = RemovePlayerPrefix(stem, item.OriginalName);
        return string.IsNullOrWhiteSpace(remainder)
            ? $"{sequence} - {alias}.osr"
            : $"{sequence} - {alias} - {remainder}.osr";
    }

    private static string RemovePlayerPrefix(string fileName, string player)
    {
        foreach (string separator in new[] { " playing ", " - " })
        {
            string prefix = player + separator;
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return fileName[prefix.Length..].Trim();
        }
        return string.Empty;
    }

    private static string UniquePath(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        string candidate = System.IO.Path.Combine(folder, fileName);
        string stem = System.IO.Path.GetFileNameWithoutExtension(fileName), extension = System.IO.Path.GetExtension(fileName);
        int number = 2;
        while (File.Exists(candidate)) candidate = System.IO.Path.Combine(folder, $"{stem} ({number++}){extension}");
        return candidate;
    }

    private void UpdateOrderAndCount()
    {
        for (int i = 0; i < Replays.Count; i++) Replays[i].Order = i + 1;
        ReplayCountText.Text = Replays.Count == 1 ? "1 replay" : $"{Replays.Count} replays";
    }
}
