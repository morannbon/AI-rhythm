# AI-rhythm v1.0.0

AI-rhythm is a TvAIr plugin that displays recommendation candidates and recommendation reasons based on TvAIr program-guide information.

It supports lightweight settings such as nickname, preferred keywords, and excluded keywords.

## Requirements

- TvAIr
- Visual Studio 2022
- .NET 8.0 Windows Desktop Runtime / SDK

## Build

1. Open `AIrhythm.BasicPlugin.sln` in Visual Studio 2022.
2. Select `Release x64`.
3. Build the solution.

The main output DLL is generated at:

```text
_bin\AIrhythm.BasicPlugin\Release\net8.0-windows\AIrhythm.BasicPlugin.dll
```

## Install

Copy the built DLL to the TvAIr plugin folder:

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.dll
```

Then restart TvAIr and open AI-rhythm from the TvAIr menu.

## Uninstall

Delete the following file after closing TvAIr:

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.dll
```

To remove AI-rhythm settings as well, delete:

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.ini
```

## Version

v1.0.0

## License

AI-rhythm is licensed under the MIT License. See `LICENSE`.

TvAIr Plugin SDK related files included under `SDK/TvAIrPlugin` follow the TvAIr distribution terms and license. See `THIRD_PARTY_NOTICES.txt`.
