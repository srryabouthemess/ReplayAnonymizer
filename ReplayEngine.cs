using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using SharpCompress.Compressors.LZMA;

namespace ReplayAnonymizer;

public sealed class ReplayItem : INotifyPropertyChanged
{
    private int order;
    private string anonymousName = string.Empty;

    public int Order { get => order; set { order = value; OnPropertyChanged(); } }
    public required string Path { get; init; }
    public required string OriginalName { get; init; }
    public string AnonymousName { get => anonymousName; set { anonymousName = value; OnPropertyChanged(); } }
    public string FileName => System.IO.Path.GetFileName(Path);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class OsrAnonymizer
{
    public static string ReadPlayerName(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int position = 5;
        ReadOsuString(data, ref position);
        return ReadOsuString(data, ref position);
    }

    public static void WriteAnonymizedCopy(string source, string destination, string newName, bool removeSmoke = false)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("O nome editado não pode ficar vazio.");
        byte[] data = File.ReadAllBytes(source);
        if (data.Length < 6) throw new InvalidDataException("Arquivo pequeno demais para ser um replay do osu!.");

        int version = BitConverter.ToInt32(data, 1);
        int position = 5;
        ReadOsuString(data, ref position);
        int playerStart = position;
        ReadOsuString(data, ref position);
        int playerEnd = position;
        ReplayLayout layout = ReadReplayLayout(data, version, playerEnd);
        byte[] replayData = removeSmoke
            ? RemoveSmokeFromReplay(data.AsSpan(layout.ReplayDataOffset, layout.ReplayDataLength).ToArray())
            : data.AsSpan(layout.ReplayDataOffset, layout.ReplayDataLength).ToArray();
        byte[]? scoreInfo = layout.ScoreInfoDataLength > 0
            ? AnonymizeLazerScoreInfo(data.AsSpan(layout.ScoreInfoDataOffset, layout.ScoreInfoDataLength).ToArray()) : null;
        byte[] encodedName = EncodeOsuString(newName.Trim());
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);

        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(data, 0, playerStart);
        output.Write(encodedName);
        output.Write(data, playerEnd, layout.ReplayLengthOffset - playerEnd);
        output.Write(BitConverter.GetBytes(replayData.Length));
        output.Write(replayData);
        int oldReplayEnd = layout.ReplayDataOffset + layout.ReplayDataLength;
        output.Write(data, oldReplayEnd, layout.OnlineIdOffset - oldReplayEnd);
        if (layout.OnlineIdLength == 8) output.Write(BitConverter.GetBytes(-1L));
        else if (layout.OnlineIdLength == 4) output.Write(BitConverter.GetBytes(-1));
        if (layout.ScoreInfoLengthOffset >= 0 && scoreInfo is not null)
        {
            int onlineIdEnd = layout.OnlineIdOffset + layout.OnlineIdLength;
            output.Write(data, onlineIdEnd, layout.ScoreInfoLengthOffset - onlineIdEnd);
            output.Write(BitConverter.GetBytes(scoreInfo.Length));
            output.Write(scoreInfo);
            int oldEnd = layout.ScoreInfoDataOffset + layout.ScoreInfoDataLength;
            output.Write(data, oldEnd, data.Length - oldEnd);
        }
        else
        {
            int remainder = layout.OnlineIdOffset + layout.OnlineIdLength;
            output.Write(data, remainder, data.Length - remainder);
        }
    }

    private static ReplayLayout ReadReplayLayout(byte[] data, int version, int position)
    {
        ReadOsuString(data, ref position);
        position += 12 + 4 + 2 + 1 + 4;
        ReadOsuString(data, ref position);
        position += 8;
        EnsureAvailable(data, position, 4);
        int replayLengthOffset = position;
        int replayLength = BitConverter.ToInt32(data, position);
        if (replayLength < 0) throw new InvalidDataException("Tamanho inválido dos frames do replay.");
        position += 4;
        int replayDataOffset = position;
        EnsureAvailable(data, position, replayLength);
        position += replayLength;
        int onlineIdOffset = position;
        int onlineIdLength = version >= 20140721 ? 8 : version >= 20121008 ? 4 : 0;
        EnsureAvailable(data, position, onlineIdLength);
        position += onlineIdLength;
        int lengthOffset = -1, dataOffset = -1, dataLength = 0;
        if (version >= 30000001 && data.Length - position >= 4)
        {
            lengthOffset = position;
            dataLength = BitConverter.ToInt32(data, position);
            if (dataLength < 0) throw new InvalidDataException("Tamanho inválido das informações do lazer.");
            position += 4;
            dataOffset = position;
            EnsureAvailable(data, position, dataLength);
        }
        return new ReplayLayout(replayLengthOffset, replayDataOffset, replayLength, onlineIdOffset, onlineIdLength, lengthOffset, dataOffset, dataLength);
    }

    private static byte[] RemoveSmokeFromReplay(byte[] compressedData)
    {
        string replay = Encoding.UTF8.GetString(DecompressLzma(compressedData));
        string[] frames = replay.Split(',');

        for (int i = 0; i < frames.Length; i++)
        {
            string[] values = frames[i].Split('|');
            if (values.Length < 4 || values[0] == "-12345" ||
                !int.TryParse(values[3], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int buttons)) continue;

            int buttonsWithoutSmoke = buttons & ~16;
            if (buttonsWithoutSmoke == buttons) continue;
            values[3] = buttonsWithoutSmoke.ToString(System.Globalization.CultureInfo.InvariantCulture);
            frames[i] = string.Join('|', values);
        }

        return CompressLzma(Encoding.UTF8.GetBytes(string.Join(',', frames)));
    }

    private static byte[] AnonymizeLazerScoreInfo(byte[] compressedData)
    {
        JsonObject scoreInfo = JsonNode.Parse(DecompressLzma(compressedData))?.AsObject()
            ?? throw new InvalidDataException("Metadados JSON do lazer inválidos.");
        SetJsonNumber(scoreInfo, "UserID", 1);
        SetJsonNumber(scoreInfo, "OnlineID", -1);
        return CompressLzma(Encoding.UTF8.GetBytes(scoreInfo.ToJsonString()));
    }

    private static void SetJsonNumber(JsonObject json, string name, long value)
    {
        string normalized = name.Replace("_", string.Empty, StringComparison.Ordinal);
        string? key = json.Select(pair => pair.Key).FirstOrDefault(candidate =>
            string.Equals(candidate.Replace("_", string.Empty, StringComparison.Ordinal), normalized, StringComparison.OrdinalIgnoreCase));
        if (key is not null) json[key] = value;
    }

    private static byte[] DecompressLzma(byte[] data)
    {
        if (data.Length < 13) throw new InvalidDataException("Bloco LZMA do lazer incompleto.");
        using var input = new MemoryStream(data, 13, data.Length - 13, false);
        using var lzma = LzmaStream.Create(data[..5], input, input.Length, BitConverter.ToInt64(data, 5), false);
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

    private static string ReadOsuString(byte[] data, ref int position)
    {
        EnsureAvailable(data, position, 1);
        byte marker = data[position++];
        if (marker == 0) return string.Empty;
        if (marker != 0x0b) throw new InvalidDataException($"Marcador de texto inválido na posição {position - 1}.");
        ulong length = ReadUleb128(data, ref position);
        if (length > int.MaxValue) throw new InvalidDataException("Campo de texto grande demais.");
        EnsureAvailable(data, position, (int)length);
        string value = Encoding.UTF8.GetString(data, position, (int)length);
        position += (int)length;
        return value;
    }

    private static ulong ReadUleb128(byte[] data, ref int position)
    {
        ulong result = 0; int shift = 0;
        while (true)
        {
            EnsureAvailable(data, position, 1);
            byte value = data[position++];
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 64) throw new InvalidDataException("ULEB128 inválido.");
        }
    }

    private static byte[] EncodeOsuString(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        using var output = new MemoryStream();
        output.WriteByte(0x0b);
        ulong length = (ulong)utf8.Length;
        do { byte part = (byte)(length & 0x7f); length >>= 7; if (length != 0) part |= 0x80; output.WriteByte(part); } while (length != 0);
        output.Write(utf8);
        return output.ToArray();
    }

    private static void EnsureAvailable(byte[] data, int position, int count)
    {
        if (position < 0 || count < 0 || position > data.Length - count) throw new InvalidDataException("Replay truncado ou inválido.");
    }

    private readonly record struct ReplayLayout(
        int ReplayLengthOffset, int ReplayDataOffset, int ReplayDataLength,
        int OnlineIdOffset, int OnlineIdLength,
        int ScoreInfoLengthOffset, int ScoreInfoDataOffset, int ScoreInfoDataLength);
}
