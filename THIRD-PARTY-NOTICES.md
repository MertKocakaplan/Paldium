# Third-party notices

Paldium is licensed under the **GNU General Public License v3.0** (`GPL-3.0-or-later`, see [LICENSE](LICENSE)). v0.1.0 was published under MIT; from v0.1.1 on, Paldium moved to GPL-3.0 because release builds bundle `libooz` (GPL-3.0). This file lists third-party components included or referenced and their license terms.

## Palworld game data and images (not covered by GPL-3.0)

`data/` (the data bundle, icons and maps), the Pal, item, skill, building and technology names and texts, and every image taken from the game come from
**Palworld**, which belongs to **Pocketpair, Inc.** They are extracted from the game files with the tools in `tools/` and are included so the app can show
game information. They are **not** licensed under the GPL-3.0 license of this project, and this project is **not** affiliated with, endorsed by or sponsored by
Pocketpair. "Palworld" and related names and images are trademarks or property of Pocketpair, Inc. If you are a rights holder and want something removed,
please open an issue.

## ooz / libooz (GPL-3.0)

`libooz` (bundled via PalCalc for Oodle-compatible decompression of Palworld save files) is licensed under the **GNU General Public License v3.0**:

- Copyright (C) 2016 Powzix and contributors
- Source repositories: [zao/ooz](https://github.com/zao/ooz), [powzix/ooz](https://github.com/powzix/ooz)
- Full license text: see [LICENSE](LICENSE).

## PalCalc (MIT)

`external/palcalc` is a git submodule of [PalCalc](https://github.com/tylercamp/palcalc) by Tyler Camp, used unmodified for reading Palworld save files and
for its breeding data. It includes `libooz` (GPL-3.0 above) and incorporates save parsing derived from [palworld-save-tools](https://github.com/cheahjs/palworld-save-tools) (MIT).

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

## palworld-save-tools (MIT)

Palworld GVAS/sav structure handling in PalCalc builds on [palworld-save-tools](https://github.com/cheahjs/palworld-save-tools) (MIT License).

## Fonts

- **IBM Plex Sans** and **IBM Plex Mono**, Copyright IBM Corp., SIL Open Font License 1.1. License texts are shipped next to the fonts in
  `src/Paldium.App/wwwroot/fonts/ibm-plex/`.

## Microsoft Edge WebView2

Paldium hosts its UI using **Microsoft Edge WebView2** (loader + WebView2 Runtime, system library licensed under the Microsoft WebView2 License).

## NuGet packages

| Package | License | Used for |
|---|---|---|
| MudBlazor | MIT | UI components |
| Microsoft.AspNetCore.Components.WebView.Wpf, Microsoft.Extensions.Localization | MIT | Blazor Hybrid host, localisation |
| Microsoft.Web.WebView2 | Microsoft WebView2 License | Embedded Chromium web view (system library) |
| Microsoft.Windows.SDK.NET, WinRT.Runtime | MIT / Microsoft .NET Library License | Windows runtime projections |
| Markdig | BSD-2-Clause | Markdown guides and license notices |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File | Apache-2.0 | Application logging |
| Costura.Fody, Fody | MIT | Native/managed assembly embedding (via PalCalc.SaveReader) |
| SharpZipLib | MIT | Archive handling (via PalCalc) |
| Newtonsoft.Json | MIT | JSON parsing (via PalCalc and `Paldium.GameExtractor`) |
| CUE4Parse | Apache-2.0 | Reading game files (`tools/Paldium.GameExtractor`, build time only) |
| KeraLua | MIT | Compile check of the mod script (tests only) |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, coverlet.collector | Apache-2.0 / MIT | Tests only |

## UE4SS

The optional assist mod (`mod/PaldiumBridge`) is a Lua script that runs inside [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (MIT). UE4SS is not included.
