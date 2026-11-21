# Third-party notices

Paldium (MIT, see [LICENSE](LICENSE)) uses the work of others. This file lists what is included or referenced and under which terms.

## Palworld game data and images (not covered by the MIT license)

`data/` (the data bundle, icons and maps), the Pal, item, skill, building and technology names and texts, and every image taken from the game come from
**Palworld**, which belongs to **Pocketpair, Inc.** They are extracted from the game files with the tools in `tools/` and are included so the app can show
game information. They are **not** licensed under the MIT license of this project, and this project is **not** affiliated with, endorsed by or sponsored by
Pocketpair. "Palworld" and related names and images are trademarks or property of Pocketpair, Inc. If you are a rights holder and want something removed,
please open an issue.

## PalCalc (MIT)

`external/palcalc` is a git submodule of [PalCalc](https://github.com/tylercamp/palcalc) by Tyler Camp, used unmodified for reading Palworld save files and
for its breeding data. It includes `libooz`, the Oodle-compatible decompressor used for Palworld saves.

> Copyright 2024, Tyler Camp
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in
> the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
> the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR
> A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN
> ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The full text is in `external/palcalc/LICENSE.txt` after cloning with `--recurse-submodules`.

## Fonts

- **IBM Plex Sans** and **IBM Plex Mono**, Copyright IBM Corp., SIL Open Font License 1.1. License texts are shipped next to the fonts in
  `src/Paldium.App/wwwroot/fonts/`.

## NuGet packages

| Package | License | Used for |
|---|---|---|
| MudBlazor | MIT | UI components |
| Microsoft.AspNetCore.Components.WebView.Wpf, Microsoft.Extensions.Localization | MIT | Blazor Hybrid host, localisation |
| Markdig | BSD-2-Clause | Markdown guides |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | Apache-2.0 | logging |
| CUE4Parse | Apache-2.0 | reading game files (`tools/Paldium.GameExtractor`, build time only) |
| KeraLua | MIT | compile check of the mod script (tests only) |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector | Apache-2.0 / MIT | tests only |

PalCalc brings its own dependencies; see its repository.

## UE4SS

The optional assist mod (`mod/PaldiumBridge`) is a Lua script that runs inside [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (MIT). UE4SS is not included.
