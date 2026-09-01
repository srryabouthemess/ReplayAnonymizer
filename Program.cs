using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SharpCompress.Compressors.LZMA;

namespace ReplayAnonymizer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--anonymize")
        {
            OsrAnonymizer.WriteAnonymizedCopy(args[1], args[2], args[3]);
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class ReplayItem
{
    public required string Path { get; init; }
    public required string OriginalName { get; init; }
    public string AnonymousName { get; set; } = string.Empty;
}

internal static class OsrAnonymizer
{
    public static string ReadPlayerName(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int position = 5; // mode byte + client version int32
        ReadOsuString(data, ref position);
        return ReadOsuString(data, ref position);
    }

    public static void WriteAnonymizedCopy(string source, string destination, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("O nome editado não pode ficar vazio.");

        byte[] data = File.ReadAllBytes(source);
        if (data.Length < 6)
            throw new InvalidDataException("Arquivo pequeno demais para ser um replay do osu!.");

        int version = BitConverter.ToInt32(data, 1);
        int position = 5;
        ReadOsuString(data, ref position); // beatmap hash
        int playerStart = position;
        ReadOsuString(data, ref position);
        int playerEnd = position;

        ReplayLayout layout = ReadReplayLayout(data, version, playerEnd);
        byte[]? anonymizedScoreInfo = layout.ScoreInfoDataLength > 0
            ? AnonymizeLazerScoreInfo(data.AsSpan(layout.ScoreInfoDataOffset, layout.ScoreInfoDataLength).ToArray())
            : null;

        byte[] encodedName = EncodeOsuString(newName.Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(data, 0, playerStart);
        output.Write(encodedName);
        output.Write(data, playerEnd, layout.OnlineIdOffset - playerEnd);

        // Remove links to the submitted score in both the legacy header and lazer metadata.
        if (layout.OnlineIdLength == 8)
            output.Write(BitConverter.GetBytes(-1L));
        else if (layout.OnlineIdLength == 4)
            output.Write(BitConverter.GetBytes(-1));

        if (layout.ScoreInfoLengthOffset >= 0 && anonymizedScoreInfo is not null)
        {
            output.Write(BitConverter.GetBytes(anonymizedScoreInfo.Length));
            output.Write(anonymizedScoreInfo);
            int oldScoreInfoEnd = layout.ScoreInfoDataOffset + layout.ScoreInfoDataLength;
            output.Write(data, oldScoreInfoEnd, data.Length - oldScoreInfoEnd);
        }
        else
        {
            int remainderOffset = layout.OnlineIdOffset + layout.OnlineIdLength;
            output.Write(data, remainderOffset, data.Length - remainderOffset);
        }
    }

    private static ReplayLayout ReadReplayLayout(byte[] data, int version, int positionAfterPlayer)
    {
        int position = positionAfterPlayer;
        ReadOsuString(data, ref position); // replay hash
        position += 12; // hit counts
        position += 4 + 2 + 1 + 4; // score, combo, perfect, mods
        ReadOsuString(data, ref position); // life graph
        position += 8; // timestamp
        EnsureAvailable(data, position, 4);
        int replayLength = BitConverter.ToInt32(data, position);
        if (replayLength < 0) throw new InvalidDataException("Tamanho inválido dos frames do replay.");
        position += 4;
        EnsureAvailable(data, position, replayLength);
        position += replayLength;

        int onlineIdOffset = position;
        int onlineIdLength = version >= 20140721 ? 8 : version >= 20121008 ? 4 : 0;
        EnsureAvailable(data, position, onlineIdLength);
        position += onlineIdLength;

        int scoreInfoLengthOffset = -1;
        int scoreInfoDataOffset = -1;
        int scoreInfoDataLength = 0;
        if (version >= 30000001 && data.Length - position >= 4)
        {
            scoreInfoLengthOffset = position;
            scoreInfoDataLength = BitConverter.ToInt32(data, position);
            if (scoreInfoDataLength < 0) throw new InvalidDataException("Tamanho inválido das informações do lazer.");
            position += 4;
            scoreInfoDataOffset = position;
            EnsureAvailable(data, position, scoreInfoDataLength);
        }

        return new ReplayLayout(onlineIdOffset, onlineIdLength, scoreInfoLengthOffset, scoreInfoDataOffset, scoreInfoDataLength);
    }

    private static byte[] AnonymizeLazerScoreInfo(byte[] compressedData)
    {
        byte[] jsonBytes = DecompressLzma(compressedData);
        JsonObject scoreInfo = JsonNode.Parse(jsonBytes)?.AsObject()
            ?? throw new InvalidDataException("Metadados JSON do lazer inválidos.");

        SetJsonNumber(scoreInfo, "UserID", 1);
        SetJsonNumber(scoreInfo, "OnlineID", -1);
        return CompressLzma(Encoding.UTF8.GetBytes(scoreInfo.ToJsonString()));
    }

    private static void SetJsonNumber(JsonObject json, string name, long value)
    {
        string normalizedName = name.Replace("_", string.Empty, StringComparison.Ordinal);
        string? actualName = json.Select(pair => pair.Key)
            .FirstOrDefault(key => string.Equals(
                key.Replace("_", string.Empty, StringComparison.Ordinal),
                normalizedName,
                StringComparison.OrdinalIgnoreCase));
        if (actualName is not null)
            json[actualName] = value;
    }

    private static byte[] DecompressLzma(byte[] data)
    {
        if (data.Length < 13) throw new InvalidDataException("Bloco LZMA do lazer incompleto.");
        byte[] properties = data[..5];
        long outputSize = BitConverter.ToInt64(data, 5);
        using var input = new MemoryStream(data, 13, data.Length - 13, writable: false);
        using var lzma = LzmaStream.Create(properties, input, input.Length, outputSize, false);
        using var output = new MemoryStream();
        lzma.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] CompressLzma(byte[] data)
    {
        using var compressed = new MemoryStream();
        byte[] properties;
        using (var lzma = LzmaStream.Create(new LzmaEncoderProperties(), false, compressed))
        {
            properties = lzma.Properties;
            lzma.Write(data);
        }

        byte[] payload = compressed.ToArray();
        using var result = new MemoryStream(13 + payload.Length);
        result.Write(properties);
        result.Write(BitConverter.GetBytes((long)data.Length));
        result.Write(payload);
        return result.ToArray();
    }

    private readonly record struct ReplayLayout(
        int OnlineIdOffset,
        int OnlineIdLength,
        int ScoreInfoLengthOffset,
        int ScoreInfoDataOffset,
        int ScoreInfoDataLength);

    private static string ReadOsuString(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, 1);
        byte marker = data[position++];
        if (marker == 0)
            return string.Empty;
        if (marker != 0x0b)
            throw new InvalidDataException($"Marcador de texto inválido na posição {position - 1}.");

        ulong length = ReadUleb128(data, ref position);
        if (length > int.MaxValue)
            throw new InvalidDataException("Campo de texto grande demais.");
        EnsureAvailable(data, position, (int)length);
        string value = Encoding.UTF8.GetString(data, position, (int)length);
        position += (int)length;
        return value;
    }

    private static ulong ReadUleb128(byte[] data, ref int position)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            EnsureAvailable(data, position, 1);
            byte value = data[position++];
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
                return result;
            shift += 7;
            if (shift >= 64)
                throw new InvalidDataException("ULEB128 inválido.");
        }
    }

    private static byte[] EncodeOsuString(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        using var output = new MemoryStream();
        output.WriteByte(0x0b);
        ulong length = (ulong)utf8.Length;
        do
        {
            byte part = (byte)(length & 0x7f);
            length >>= 7;
            if (length != 0)
                part |= 0x80;
            output.WriteByte(part);
        } while (length != 0);
        output.Write(utf8);
        return output.ToArray();
    }

    private static void EnsureAvailable(byte[] data, int position, int count)
    {
        if (position < 0 || count < 0 || position > data.Length - count)
            throw new InvalidDataException("Replay truncado ou inválido.");
    }
}

internal sealed class MainForm : Form
{
    private readonly DataGridView grid = new();
    private readonly BindingSource binding = new();
    private readonly List<ReplayItem> items = [];
    private readonly TextBox outputFolder = new();
    private readonly TextBox manualAlias = new();
    private readonly CheckBox repeatManualAliases = new();
    private readonly CheckBox samePlayerSameAlias = new();
    private readonly ComboBox outputNaming = new();
    private readonly Label status = new();

    private const string namingAliasAndMap = "Alias + informações do mapa (Artista, música, dificuldade, etc e tals)";
    private const string namingAliasAndNumber = "Alias + número";
    private const string namingOriginal = "Manter nome original do arquivo";

    public MainForm()
    {
        Text = "Replay Anonymizer para osu!";
        MinimumSize = new Size(820, 520);
        Size = new Size(980, 650);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        Font = new Font("Segoe UI", 10);

        var heading = new Label
        {
            Text = "Anonimizador de replays do osu!",
            Font = new Font("Segoe UI Semibold", 18),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 3)
        };
        var description = new Label
        {
            Text = "Arraste replays para esta janela ou selecione vários arquivos. Os originais não serão alterados.",
            AutoSize = true,
            ForeColor = Color.DimGray
        };

        var addFiles = new Button { Text = "Adicionar replays…", AutoSize = true };
        var addFolder = new Button { Text = "Adicionar pasta…", AutoSize = true };
        var remove = new Button { Text = "Remover selecionados", AutoSize = true };
        var randomize = new Button { Text = "Gerar novos nomes", AutoSize = true };
        addFiles.Click += (_, _) => AddFiles();
        addFolder.Click += (_, _) => AddFolder();
        remove.Click += (_, _) => RemoveSelected();
        randomize.Click += (_, _) => GenerateAliases();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.AddRange([addFiles, addFolder, remove, randomize]);

        manualAlias.PlaceholderText = "Ex.: Cookiezi, mrekk, Lifeline";
        manualAlias.Dock = DockStyle.Fill;
        var applyAlias = new Button { Text = "Aplicar aos selecionados", AutoSize = true };
        applyAlias.Click += (_, _) => ApplyManualAlias();
        repeatManualAliases.Text = "Repetir nomes até preencher todos";
        repeatManualAliases.Checked = true;
        repeatManualAliases.AutoSize = true;
        repeatManualAliases.Anchor = AnchorStyles.Left;
        var aliasRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
        aliasRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        aliasRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        aliasRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        aliasRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        aliasRow.Controls.Add(new Label { Text = "Alias manual:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        aliasRow.Controls.Add(manualAlias, 1, 0);
        aliasRow.Controls.Add(repeatManualAliases, 2, 0);
        aliasRow.Controls.Add(applyAlias, 3, 0);

        grid.Dock = DockStyle.Fill;
        grid.AutoGenerateColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.RowHeadersVisible = false;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Arquivo", DataPropertyName = "Path", FillWeight = 55, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nome original", DataPropertyName = "OriginalName", FillWeight = 20, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nome editado", DataPropertyName = "AnonymousName", FillWeight = 25 });
        binding.DataSource = items;
        grid.DataSource = binding;

        outputFolder.Text = string.Empty;
        outputFolder.PlaceholderText = "Escolha uma pasta de saída…";
        outputFolder.ReadOnly = true;
        outputFolder.Dock = DockStyle.Fill;
        var chooseOutput = new Button { Text = "Escolher…", AutoSize = true };
        chooseOutput.Click += (_, _) => ChooseOutputFolder();
        samePlayerSameAlias.Text = "Usar o mesmo nome (alterado) para o mesmo jogador";
        samePlayerSameAlias.Checked = true;
        samePlayerSameAlias.AutoSize = true;

        var outputRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        outputRow.Controls.Add(new Label { Text = "Salvar em:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        outputRow.Controls.Add(outputFolder, 1, 0);
        outputRow.Controls.Add(chooseOutput, 2, 0);

        outputNaming.DropDownStyle = ComboBoxStyle.DropDownList;
        outputNaming.Items.AddRange([namingAliasAndMap, namingAliasAndNumber, namingOriginal]);
        outputNaming.SelectedIndex = 0;
        outputNaming.Dock = DockStyle.Fill;
        var namingRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        namingRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        namingRow.Controls.Add(new Label { Text = "Nome dos arquivos:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        namingRow.Controls.Add(outputNaming, 1, 0);

        var process = new Button
        {
            Text = "Aplicar edições",
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6),
            Anchor = AnchorStyles.Right
        };
        process.Click += (_, _) => ProcessFiles();
        status.AutoSize = true;
        status.ForeColor = Color.DimGray;
        status.Anchor = AnchorStyles.Left;

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(process, 1, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            RowCount = 9,
            ColumnCount = 1
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(description, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(aliasRow, 0, 3);
        layout.Controls.Add(grid, 0, 4);
        layout.Controls.Add(samePlayerSameAlias, 0, 5);
        layout.Controls.Add(outputRow, 0, 6);
        layout.Controls.Add(namingRow, 0, 7);
        layout.Controls.Add(footer, 0, 8);
        Controls.Add(layout);

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                AddPaths(paths.SelectMany(ExpandPath));
        };
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog { Filter = "Replays do osu! (*.osr)|*.osr|Todos os arquivos (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddPaths(dialog.FileNames);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Escolha uma pasta com replays" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AddPaths(Directory.EnumerateFiles(dialog.SelectedPath, "*.osr", SearchOption.TopDirectoryOnly));
    }

    private IEnumerable<string> ExpandPath(string path)
    {
        if (Directory.Exists(path))
            return Directory.EnumerateFiles(path, "*.osr", SearchOption.TopDirectoryOnly);
        return File.Exists(path) ? [path] : [];
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        var existing = items.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths.Where(p => p.EndsWith(".osr", StringComparison.OrdinalIgnoreCase)))
        {
            string fullPath = Path.GetFullPath(path);
            if (!existing.Add(fullPath)) continue;
            try
            {
                items.Add(new ReplayItem { Path = fullPath, OriginalName = OsrAnonymizer.ReadPlayerName(fullPath) });
                added++;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Não foi possível ler:\n{path}\n\n{ex.Message}", "Replay inválido", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        GenerateAliases();
        binding.ResetBindings(false);
        status.Text = added == 0 ? "Nenhum replay novo foi adicionado." : $"{added} replay(s) adicionado(s).";
    }

    private void RemoveSelected()
    {
        var selected = grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as ReplayItem)
            .Where(item => item is not null)
            .Cast<ReplayItem>()
            .ToList();
        foreach (var item in selected) items.Remove(item);
        binding.ResetBindings(false);
        status.Text = $"{items.Count} replay(s) na lista.";
    }

    private void GenerateAliases()
    {
        var byPlayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ReplayItem item in items)
        {
            if (samePlayerSameAlias.Checked && byPlayer.TryGetValue(item.OriginalName, out string? alias))
            {
                item.AnonymousName = alias;
                continue;
            }
            do { alias = "Player-" + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6); }
            while (!used.Add(alias));
            item.AnonymousName = alias;
            if (samePlayerSameAlias.Checked) byPlayer[item.OriginalName] = alias;
        }
        binding.ResetBindings(false);
    }

    private void ApplyManualAlias()
    {
        string[] aliases = manualAlias.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (aliases.Length == 0)
        {
            MessageBox.Show(this, "Digite pelo menos um alias. Para usar vários, separe-os por vírgulas.", "Alias vazio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            manualAlias.Focus();
            return;
        }

        var selected = grid.SelectedRows.Cast<DataGridViewRow>()
            .OrderBy(row => row.Index)
            .Select(row => row.DataBoundItem as ReplayItem)
            .Where(item => item is not null)
            .Cast<ReplayItem>()
            .ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Selecione pelo menos um replay na tabela.", "Nenhum replay selecionado", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int count = repeatManualAliases.Checked ? selected.Count : Math.Min(selected.Count, aliases.Length);
        for (int i = 0; i < count; i++)
            selected[i].AnonymousName = aliases[i % aliases.Length];
        binding.ResetBindings(false);
        string repetition = repeatManualAliases.Checked && selected.Count > aliases.Length
            ? " A lista foi repetida."
            : string.Empty;
        status.Text = $"{aliases.Length} nome(s) distribuído(s) entre {count} replay(s).{repetition}";
    }

    private void ChooseOutputFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Escolha onde salvar as cópias", SelectedPath = outputFolder.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            outputFolder.Text = dialog.SelectedPath;
    }

    private void ProcessFiles()
    {
        grid.EndEdit();
        if (items.Count == 0)
        {
            MessageBox.Show(this, "Adicione pelo menos um replay.", "Nada para processar", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(outputFolder.Text))
        {
            MessageBox.Show(this, "Escolha uma pasta de saída.", "Pasta necessária", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int completed = 0;
        var failures = new List<string>();
        for (int index = 0; index < items.Count; index++)
        {
            ReplayItem item = items[index];
            try
            {
                string safeAlias = string.Concat(item.AnonymousName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                string outputFileName = BuildOutputFileName(item, safeAlias, index + 1);
                string destination = UniquePath(outputFolder.Text, outputFileName);
                OsrAnonymizer.WriteAnonymizedCopy(item.Path, destination, item.AnonymousName);
                completed++;
            }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(item.Path)}: {ex.Message}"); }
        }

        status.Text = $"Concluído: {completed} cópia(s) criada(s).";
        string message = $"{completed} replay(s) anonimizado(s) com sucesso.";
        if (failures.Count > 0) message += $"\n\nFalhas:\n{string.Join("\n", failures)}";
        MessageBox.Show(this, message, "Processamento concluído", MessageBoxButtons.OK,
            failures.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private string BuildOutputFileName(ReplayItem item, string safeAlias, int number)
    {
        string originalFileName = Path.GetFileName(item.Path);
        if (outputNaming.SelectedItem?.ToString() == namingOriginal)
            return originalFileName;
        if (outputNaming.SelectedItem?.ToString() == namingAliasAndNumber)
            return $"{safeAlias} - {number:D3}.osr";

        string stem = Path.GetFileNameWithoutExtension(originalFileName);
        string remainder = RemoveOriginalPlayerPrefix(stem, item.OriginalName);
        return string.IsNullOrWhiteSpace(remainder)
            ? $"{safeAlias} - {number:D3}.osr"
            : $"{safeAlias} - {remainder}.osr";
    }

    private static string RemoveOriginalPlayerPrefix(string fileName, string playerName)
    {
        string[] separators = [" playing ", " - "];
        foreach (string separator in separators)
        {
            string prefix = playerName + separator;
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return fileName[prefix.Length..].Trim();
        }
        return string.Empty;
    }

    private static string UniquePath(string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        string candidate = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        int number = 2;
        while (File.Exists(candidate)) candidate = Path.Combine(folder, $"{stem} ({number++}){extension}");
        return candidate;
    }
}
