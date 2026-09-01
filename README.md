# Replay Anonymizer for osu!

A small Windows application for anonymizing one or many osu! replay files (`.osr`). It supports both osu!stable and osu!lazer replays.

Unlike legacy replay editors, it changes only the player-name field and preserves the rest of the file byte-for-byte, including lazer's additional score-information block.

## Features

- Drag and drop individual replays or folders.
- Process many replays at once.
- Generate unique random aliases.
- Manually edit aliases or apply one custom alias to multiple selected replays.
- Optionally use the same alias for every replay belonging to the same player.
- Always create separate copies; source files are never modified.

## Usage

1. Download `ReplayAnonymizer.exe` from the latest release.
2. Add `.osr` files by dragging them into the window or using **Adicionar replays**.
3. Review the generated aliases, edit the table directly, or select replays and apply a custom alias.
4. Choose an output folder and select **Criar cópias anonimizadas**.

## Building

Requires the .NET 9 SDK on Windows.

```powershell
dotnet build -c Release
```

To create a portable 64-bit Windows executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

## How it works

The `.osr` header stores the player name as a UTF-8 osu! string. Replay Anonymizer replaces only that encoded field and copies every following byte unchanged. It does not decompress or rebuild replay frames.

## Current limitations

- Windows GUI only.
- The generated executable is not digitally signed, so Windows SmartScreen may display a warning.
