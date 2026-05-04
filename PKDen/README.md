# PKDen

A `.pk` file viewer, organizer, and backup tool for Pokémon across all main-series games (Generations 1–9). Powered by [PKHeX](https://github.com/kwsch/PKHeX).

## What is PKDen?

PKDen is a **library for your Pokémon collection**. It lets you pull Pokémon out of any save file and organize them across dozens of custom-named Dens (30 slots each), search across everything, sort, annotate, and export back as `.pk` files compatible with PKHeX.

**PKDen is NOT a save editor.** Save files are opened in read-only mode — PKDen never modifies your original save. For editing Pokémon or save files directly, use [PKHeX](https://github.com/kwsch/PKHeX).

## Features

- Organize Pokémon across 50+ custom-named Dens (30 slots each)
- Import `.pk` files (Gen 1–9: pk1 through pk9) from folders or individual files
- Copy Pokémon from any save file into PKDen for long-term storage
- Export as PKHeX-compatible `.pk` files — filenames match PKHeX's naming scheme
- Export by generation — pull every `.pk1` in one action, for example
- Search across all Dens by species name or Original Trainer
- Sort Dens by species, level, IVs, shiny status, OT, and more
- Per-Pokémon notes and transfer timestamps
- Custom background images per Den or globally
- Detailed summary panel: nature, ability, IVs, moves, held item, origin game
- Undo (up to 20 levels)
- Recently Deleted panel — recover accidentally removed Pokémon
- View party Pokémon alongside boxes when a save is loaded
- Drag & drop between save, party, and Dens
- Window size and split position persist between sessions

## Building

Requires .NET 10 SDK on Windows.

```bash
cd PKHeX-master
dotnet publish PKDen\PKDen.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `PKDen\bin\Release\net10.0-windows\win-x64\publish\PKDen.exe`

## Data Storage

PKDen is fully portable — no registry, no AppData. Two files next to the executable:

- `PKDen.den` — your Pokémon collection (custom binary format)
- `PKDen.settings` — view preferences (sprite size, labels, window layout)

A `backgrounds/` folder is created if you set custom Den backgrounds.

## Credits

PKDen uses [PKHeX](https://github.com/kwsch/PKHeX) (version 26.04.11, GPL-3.0) by [kwsch](https://github.com/kwsch) and contributors. All sprite assets, species data, move/ability strings, entity parsing, and save-file format handling come from PKHeX. Huge thanks to the PKHeX team for making such a robust library available.

## License

GPL-3.0 (inherited from PKHeX). See [LICENSE](LICENSE).

## Disclaimer

PKDen is a fan-made tool. Pokémon and all related trademarks are property of Nintendo, Game Freak, and The Pokémon Company. This project is not affiliated with or endorsed by any of these entities.
