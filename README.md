# Replay Anonymizer for osu!

[![Build](https://github.com/srryabouthemess/ReplayAnonymizer/actions/workflows/build.yml/badge.svg)](https://github.com/srryabouthemess/ReplayAnonymizer/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A small Windows application for anonymizing one or many osu! replay files (`.osr`). It supports both osu!stable and osu!lazer replays.

The app uses a custom dark WPF interface designed for quick event preparation and large replay batches.

Unlike legacy replay editors, it preserves replay frames while anonymizing both the visible player-name field and the account identifiers stored in lazer's additional score-information block.

## Features

- Drag and drop individual replays or folders.
- Accept file and folder drops anywhere in the app, including directly over the replay table.
- Process many replays at once.
- Reorder replays before exporting; table order controls alias distribution and output numbering.
- Remove selected replays with the Delete key.
- Remove one or many selected replays from a right-click context menu.
- Show the current replay count and sequence number for every row.
- Generate unique random aliases.
- Manually edit aliases or distribute a comma-separated list among selected replays.
- Optionally repeat a shorter alias list until every selected replay has a name.
- Choose safe output filenames: alias plus map information, alias plus sequence number, or the original filename.
- Prefix every exported file with its three-digit table position so folder order matches app order.
- Optionally use the same alias for every replay belonging to the same player.
- Always create separate copies; source files are never modified.

## Usage

1. Download `ReplayAnonymizer.exe` from the [latest release](https://github.com/srryabouthemess/ReplayAnonymizer/releases/latest).
2. Add `.osr` files by dragging them into the window or using **Adicionar replays**.
3. Review the generated aliases, edit the table directly, or select replays and enter custom aliases separated by commas.
4. Choose an output folder and select **Aplicar edições**.

The default output naming mode removes the original player name from the filename. When the filename does not follow a recognised osu! pattern, the app safely falls back to the alias plus a sequence number.

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

The `.osr` header stores the player name as a UTF-8 osu! string. Replay Anonymizer replaces that field and removes score/account identifiers that would allow lazer to resolve the original username and avatar. Replay frames are copied unchanged and are never decompressed or rebuilt.

## Privacy

All processing happens locally on your computer. Replay Anonymizer does not upload replays, contact an external service, collect analytics, or store player information. Original files are opened read-only and anonymized copies are written only to the folder selected by the user.

## Reporting bugs

Please open a [GitHub issue](https://github.com/srryabouthemess/ReplayAnonymizer/issues) with a description of the problem and the osu! client version involved. Replays may contain player and score identifiers, so do not attach a non-anonymized replay to a public issue unless you are comfortable sharing that information.

## Current limitations

- Windows GUI only.
- The generated executable is not digitally signed, so Windows SmartScreen may display a warning.

## License

Replay Anonymizer is available under the [MIT License](LICENSE). See [third-party notices](THIRD-PARTY-NOTICES.md) for bundled dependencies.

This is an independent community project and is not affiliated with or endorsed by ppy Pty Ltd or the osu! team. osu! is a trademark of ppy Pty Ltd.
