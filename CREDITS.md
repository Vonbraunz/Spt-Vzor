# Credits and licences

## chams.bundle

The shader asset bundle (`assets\chams.bundle`, 18,806 bytes, built with Unity
2022.3.62f3) was extracted from **TarkovAddons v1.1 Hotfix 4**, published on
unknowncheats.me. The upstream release states:

> ALL FILES PROVIDED (UNLESS STATED OTHERWISE) ARE PROVIDED UNDER THE CC
> Attribution-NonCommercial-ShareAlike 4.0 International LICENSE

So the bundle is licensed **CC BY-NC-SA 4.0**, see `LICENSE-ASSETS`. That means:
credit the original author, no commercial use, and share alike. The plugin code
around it is MIT, but the bundle is not, so do not pretend otherwise if you
redistribute it.

## Flea price data

`prices-regular.json` and `prices-pve.json` are data files from
**[SPT-LiveFleaPriceDB](https://github.com/DrakiaXYZ/SPT-LiveFleaPriceDB)** by DrakiaXYZ,
used under the MIT licence:

```
MIT License

Copyright (c) 2023 DrakiaXYZ

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## What was not carried over

Nothing else from that release. Specifically: no BattlEye disabler, no HWID
spoofer, no launcher injector, no PEB module unlinking, no il2cpp pattern
scanners. This is a BepInEx plugin for an offline single-player install. It has
no business hiding from anything.

## This plugin

Written for SPT 5.0 (EFT 1.2.0.47965 / Unity 2022.3.43f2 / BepInEx 6 IL2CPP).
```
- GameWorld.AllAlivePlayersList -> EFT.Player
- Player.IsYourPlayer, Player.PlayerBody
- PlayerBody.BodySkins (Dictionary<EBodyModelPart, LoddedSkin>)
- LoddedSkin._lods -> Diz.Skinning.AbstractSkin
- AbstractSkin.SkinnedMeshRenderer -> Renderer.sharedMaterials
```

## Instant search patches

The instant search patches in `InstantSearchPatch.cs` are ported from
**[asler/spt-loot-instant-search](https://github.com/asler/spt-loot-instant-search)**.
That repository ships an MIT licence (note: the upstream `LICENSE.txt` still contains
the unmodified template placeholders `[year] [fullname]`, so no copyright holder is
named there). The MIT terms apply:

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
