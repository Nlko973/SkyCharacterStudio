# SkyCharacterStudio

**SkyCharacterStudio** is a Windows desktop editor for viewing and editing Sky: Children of the Light character assets.

> **Pre-Alpha:** This project is an early work in progress. Features, compatibility, and stability may change.

## Requirements

- Windows 10 or later
- .NET 9 SDK to build from source
- A legally obtained installation of *Sky: Children of the Light* for local asset access

## Build

Open `SkyCharacterStudio.csproj` in Visual Studio 2022 or run:

```powershell
dotnet build SkyCharacterStudio.csproj -c Release
```

The editor reads game files locally from the installation selected by the user. Game files and assets are not included in this repository.

## Ownership and attribution

All rights to *Sky: Children of the Light*, including its game content, assets, names, and trademarks, belong to Thatgamecompany (TGC). This editor uses locally installed game resources for its functionality; it does not claim ownership of or redistribute those resources.

The SkyCharacterStudio editor and its original source code belong to the project authors. The editor is an independent, unofficial project and is not affiliated with, endorsed by, or sponsored by TGC.
