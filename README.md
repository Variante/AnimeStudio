# Anime Studio
## Asset extraction tool for unity games !

![image](https://github.com/user-attachments/assets/fc1decdc-a589-43a2-b965-2d8151d0975f)

---

# Endfield export build

This fork carries the AnimeStudio CLI changes used by
`Variante/endfield_research_kit` to decode Endfield Unity assets for its WebUI
export flow. The important day-to-day output is:

```text
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe
```

## Standalone build

Install a .NET 9 SDK, then build the CLI from this repository root:

```powershell
dotnet restore AnimeStudio.CLI\AnimeStudio.CLI.csproj -p:RestoreIgnoreFailedSources=true -p:NuGetAudit=false
dotnet build AnimeStudio.CLI\AnimeStudio.CLI.csproj -c Release -f net9.0-windows
```

The executable will be written to:

```text
AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe
```

The GUI and patcher projects can be built the same way:

```powershell
dotnet build AnimeStudio.GUI\AnimeStudio.GUI.csproj -c Release -f net9.0-windows
dotnet build AnimeStudio.Patcher\AnimeStudio.Patcher.csproj -c Release -f net9.0
```

## Build inside endfield_research_kit

When this repository is checked out as `tools\AnimeStudio` inside
`endfield_research_kit`, the parent repository provides helper scripts that
install an isolated .NET 9 SDK under `tools\AnimeStudio\.dotnet` and build the
managed projects:

```powershell
.\scripts\animestudio\setup_dotnet9.bat
.\scripts\animestudio\rebuild.bat -Target CLI
```

After the first restore, a faster rebuild is usually:

```powershell
.\scripts\animestudio\rebuild.bat -Target CLI -NoRestore
```

The helper prints the output path, which should be:

```text
tools\AnimeStudio\AnimeStudio.CLI\bin\Release\net9.0-windows\AnimeStudio.CLI.exe
```

`endfield_research_kit` expects that executable before running
`export.bat --export-from-game` or `export_assets.bat --export-from-game`.

## Native projects

The common Endfield export flow only rebuilds the managed projects. To rebuild
native projects such as `AnimeStudio.FBXNative` or `AnimeStudio.Ooz`, install
Visual Studio 2022 Build Tools with the Desktop development with C++ workload,
then build `AnimeStudio.sln` from a Developer PowerShell or Visual Studio.

---

# How do I use this ?

You should look at the [official wiki](https://github.com/Escartem/AnimeStudio/wiki), if required look at the [original tutorial by Modder4869](https://gist.github.com/Modder4869/0f5371f8879607eb95b8e63badca227e) or the [original readme](https://github.com/RazTools/Studio/blob/main/README.md). Otherwise [join the discord](https://discord.gg/fzRdtVh) and ask there !

---

# How do I download this ?

## [Download Studio for .NET 9 (recommended ✨)](https://nightly.link/Escartem/AnimeStudio/workflows/build/master/AnimeStudio-net9.zip) or [Download Studio for .NET 8](https://nightly.link/Escartem/AnimeStudio/workflows/build/master/AnimeStudio-net8.zip)

---

# What is this ?

It's an up-to-date fork of Razmoth's one. After his repo was discontinued, bugs started to arise as games evolved, and people started making forks to fix some of them, but each one would not support the fixes by the others and so on. This version aims at being the new start base for AssetStudio, renamed as AnimeStudio, it supports all 3 main hoyo games, and is open to any contribution !

---

# What changed ?

This is a non-exhaustive list of modifications :
- Removed usage of a [certain dll for a certain decryption](https://github.com/Escartem/AnimeStudio/commit/1fcfa9041e07cd0a98b4d23f1578e910256fa1f8) 👀
- Merged fixes for Genshin, Star Rail and ZZZ suport with improvements
- Dark mode
- Reorganised menu bar for easier usage
- Addes SHA256 hash for assets
- New game selector merged with UnityCN keys list and updated UnityCN keys manager
- Asset Browser improvements
    - It is now possible to use json files instead of only message pack
    - You can now relocate the sources files of a map instead of having to build a new one to adjust them, making maps no longer game install dir dependant
    - Only selected assets are displayed in the main window when loading instead of the full blocks
    - You can load 2 asset maps at once and view the difference between both

---

Special thanks to:
- [hrothgar](https://github.com/hrothgar234567): Help in ZZZ fixes & some dll RE
- Razmoth: Original AssetStudio for anime games support - [[project](https://github.com/RazTools/Studio)]
- hashblen: ZZZ fixes fork - [[project](https://github.com/hashblen/ZZZ_Studio)]
- yarik0chka: Genshin and Star Rail fixes fork - [[project](https://github.com/yarik0chka/YarikStudio)]
- aelurum: AssetStudioMod - [[project](https://github.com/aelurum/AssetStudio)]
- Perfare: The real original AssetStudio - [[project](https://github.com/perfare/AssetStudio)]
- All of the others contributor of Razmoth's Studio
