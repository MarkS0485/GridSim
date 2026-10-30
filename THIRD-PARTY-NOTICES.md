# Third-party notices

GridSim itself is licensed under the terms in [LICENSE.md](LICENSE.md). The binaries GridSim ships (the `gridsim`
command line tool and the GridSim web app container image; the desktop app, which ships after 1.0, and source builds
carry the same components) also contain or depend on the third-party components listed below. Each package is used
unmodified under its own licence, reproduced or referenced here as that licence requires. The one piece of
third-party SOURCE code compiled into GridSim itself, including the GridSim.Core NuGet package, is the C# port of
FDLIBM listed in the second table.

This file ships next to every published binary and inside the container image. `ThirdPartyNoticesTests` fails when a
NuGet package that ships in the CLI, web or desktop app is missing from the table below, so the list cannot drift.

Licence information was read from each package's own `.nuspec` and licence files in the NuGet cache on 2026-10-03
(the K4os.Compression.LZ4 package declares only a licence URL; its repository's LICENSE file is MIT). The Avalonia
desktop app's packages (UI programme 0.4) were read the same way on 2026-10-20; the Inter font's copyright, version
and licence were read from the font's own name table inside Avalonia.Fonts.Inter.

Bundled third-party network DATA (as opposed to code) is listed under "Bundled network data" at the end of this file.

## Components

| Component | Version | Licence | Copyright | Used by | Source |
|---|---|---|---|---|---|
| ILGPU | 1.5.3 | University of Illinois/NCSA Open Source License | Copyright (c) 2016-2025 ILGPU Project | GridSim.Gpu (CLI) | http://www.ilgpu.net/ |
| ILGPU.Algorithms | 1.5.3 | University of Illinois/NCSA Open Source License | Copyright (c) 2016-2025 ILGPU Project | GridSim.Gpu (CLI) | http://www.ilgpu.net/ |
| Parquet.Net | 6.1.0 | MIT | Copyright (c) the Parquet.Net authors (the package declares no copyright line; see its LICENSE) | GridSim.Gda (CLI) | https://github.com/aloneguid/parquet-dotnet |
| Snappier | 1.3.1 | BSD-3-Clause | Copyright 2011-2020, Google, Inc. and Snappier Authors | Parquet.Net | https://brantburnett.github.io/Snappier/ |
| ZstdSharp.Port | 0.8.8 | MIT | Copyright Oleg Stepanischev | Parquet.Net | https://github.com/oleg-st/ZstdSharp |
| K4os.Compression.LZ4 | 1.3.8 | MIT | Copyright (c) Milosz Krajewski | Parquet.Net | https://github.com/MiloszKrajewski/K4os.Compression.LZ4 |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT | Copyright (c) Microsoft Corporation | Parquet.Net | https://github.com/Microsoft/Microsoft.IO.RecyclableMemoryStream |
| CommunityToolkit.HighPerformance | 8.4.2 | MIT | Copyright (c) .NET Foundation and Contributors | Parquet.Net | https://github.com/CommunityToolkit/dotnet |
| Microsoft.AspNetCore.App.Internal.Assets | 10.0.12 | MIT | Copyright (c) .NET Foundation and Contributors | GridSim.Web (Blazor framework scripts) | https://github.com/dotnet/aspnetcore |
| Avalonia | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | GridSim.App (Avalonia desktop app) | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Themes.Fluent | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | GridSim.App | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | 11.3.22 | MIT (the package); the Inter font it embeds is SIL Open Font License 1.1 (row below) | Copyright 2013-2026 (c) The AvaloniaUI Project | GridSim.App | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Skia | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | GridSim.App | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Remote.Protocol | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | Avalonia | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.BuildServices | 11.3.2 | MIT | Copyright 2023-2025 (c) The AvaloniaUI Project | Avalonia (build time only: an MSBuild task; nothing from it is copied to the output) | https://avaloniaui.net/ |
| Avalonia.Desktop | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | GridSim.App.Desktop | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Win32 | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | Avalonia.Desktop (Windows) | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.X11 | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | Avalonia.Desktop (Linux) | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.FreeDesktop | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | Avalonia.X11 (Linux) | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Native | 11.3.22 | MIT | Copyright 2013-2026 (c) The AvaloniaUI Project | Avalonia.Desktop (macOS) | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Angle.Windows.Natives | 2.1.25547.20250602 | BSD-3-Clause (the ANGLE licence, reproduced below) | Copyright 2018 The ANGLE Project Authors | Avalonia.Win32 (Windows OpenGL ES over Direct3D) | https://github.com/AvaloniaUI/angle |
| MicroCom.Runtime | 0.11.0 | MIT | Copyright 2021 (c) Nikita Tsukanov | Avalonia | https://github.com/kekekeks/MicroCom |
| Tmds.DBus.Protocol | 0.21.3 | MIT | Copyright Tom Deseyn | Avalonia.FreeDesktop (Linux) | https://github.com/tmds/Tmds.DBus |
| SkiaSharp | 2.88.9 | MIT (the .NET binding); the native Skia library is BSD-3-Clause (reproduced below) | (c) Microsoft Corporation. All rights reserved. | Avalonia.Skia (2D rendering) | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT; native Skia BSD-3-Clause and the components in SkiaSharp's THIRD-PARTY-NOTICES.txt | (c) Microsoft Corporation. All rights reserved. | SkiaSharp (Windows) | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.Linux | 2.88.9 | MIT; native Skia BSD-3-Clause and the components in SkiaSharp's THIRD-PARTY-NOTICES.txt | (c) Microsoft Corporation. All rights reserved. | SkiaSharp (Linux) | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.macOS | 2.88.9 | MIT; native Skia BSD-3-Clause and the components in SkiaSharp's THIRD-PARTY-NOTICES.txt | (c) Microsoft Corporation. All rights reserved. | SkiaSharp (macOS) | https://github.com/mono/SkiaSharp |
| SkiaSharp.NativeAssets.WebAssembly | 2.88.9 | MIT; native Skia BSD-3-Clause and the components in SkiaSharp's THIRD-PARTY-NOTICES.txt | (c) Microsoft Corporation. All rights reserved. | SkiaSharp (browser builds only; restored, not used by the desktop app) | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp | 8.3.1.1 | MIT (the .NET binding); the native HarfBuzz library is the "Old MIT" licence (reproduced below) | (c) Microsoft Corporation. All rights reserved. | Avalonia.Skia (text shaping) | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT; native HarfBuzz "Old MIT" | (c) Microsoft Corporation. All rights reserved. | HarfBuzzSharp (Windows) | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp.NativeAssets.Linux | 8.3.1.1 | MIT; native HarfBuzz "Old MIT" | (c) Microsoft Corporation. All rights reserved. | HarfBuzzSharp (Linux) | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp.NativeAssets.macOS | 8.3.1.1 | MIT; native HarfBuzz "Old MIT" | (c) Microsoft Corporation. All rights reserved. | HarfBuzzSharp (macOS) | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp.NativeAssets.WebAssembly | 8.3.1.1 | MIT; native HarfBuzz "Old MIT" | (c) Microsoft Corporation. All rights reserved. | HarfBuzzSharp (browser builds only; restored, not used by the desktop app) | https://github.com/mono/SkiaSharp |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Copyright (c) .NET Foundation and Contributors | GridSim.App (view models) | https://github.com/CommunityToolkit/dotnet |
| Inter (font, embedded in Avalonia.Fonts.Inter) | 3.019 | SIL Open Font License 1.1 (reproduced below) | Copyright 2020 The Inter Project Authors (https://github.com/rsms/inter) | GridSim.App.Desktop (the default user-interface font) | https://rsms.me/inter/ |
| .NET runtime, ASP.NET Core and Windows Desktop runtime | 10.0 | MIT | Copyright (c) .NET Foundation and Contributors | every self-contained binary and the container image | https://github.com/dotnet/runtime |

The self-contained binaries and the container image carry the .NET runtime itself. Its own third-party notices
are published at https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT and, for ASP.NET Core, at
https://github.com/dotnet/aspnetcore/blob/main/THIRD-PARTY-NOTICES.txt.

ILGPU in turn uses System.Collections.Immutable, System.Memory, System.Reflection.Metadata and
System.Runtime.CompilerServices.Unsafe (all MIT, part of .NET) and parts of the .NET runtime source (MIT), as its
own LICENSE-3RD-PARTY.txt records.

The test projects' packages (xUnit, Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing, Avalonia.Headless.XUnit
and the Avalonia.Headless platform it brings) are used only to test GridSim and are not shipped.

The SkiaSharp and HarfBuzzSharp native-asset packages carry native builds of Skia and HarfBuzz. SkiaSharp's own
notices file also lists the further open-source libraries a Skia build can include (ANGLE, etc1, gif, libpng, DNG SDK,
expat, FreeType, ICU, imgui, jsoncpp, libjpeg-turbo, libwebp, libmicrohttpd, piex, SDL, sfntly, SPIR-V Headers,
SPIR-V Tools and zlib). Those notices are in the THIRD-PARTY-NOTICES.txt file inside each of those packages and at
https://github.com/mono/SkiaSharp/blob/main/THIRD-PARTY-NOTICES.txt; the Skia and HarfBuzz licences themselves are
reproduced below.

## Ported source code

| Component | Version | Licence | Copyright | Used by | Source |
|---|---|---|---|---|---|
| FDLIBM (Freely Distributable LIBM) | 5.3 | Sun Microsystems notice (permissive, reproduced below) | Copyright (C) 1993 and 2004 Sun Microsystems, Inc. | GridSim.Core `Numerics/Fdlibm.*.cs` (front door `DetMath`): GridSim's deterministic sin, cos, tan, asin, acos, atan, atan2, exp, expm1, log, log10, pow, sinh, cosh (D1, October 2026) | https://www.netlib.org/fdlibm/ |

GridSim's files are a line-by-line C# translation of these FDLIBM 5.3 files, made from the original C sources:
k_sin.c, k_cos.c, k_tan.c, e_rem_pio2.c, k_rem_pio2.c, s_sin.c, s_cos.c, s_tan.c, s_scalbn.c, s_atan.c,
e_atan2.c, e_asin.c, e_acos.c, e_exp.c, s_expm1.c, e_log.c, e_log10.c, e_pow.c, e_sinh.c and e_cosh.c. Each
translated file keeps the notice at its top. The notice, as it appears in the 1993 files:

```
====================================================
Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.

Developed at SunSoft, a Sun Microsystems, Inc. business.
Permission to use, copy, modify, and distribute this
software is freely granted, provided that this notice
is preserved.
====================================================
```

and as it appears in the 2004 files (k_tan.c, e_exp.c, s_expm1.c, e_pow.c):

```
====================================================
Copyright (C) 2004 by Sun Microsystems, Inc. All rights reserved.

Permission to use, copy, modify, and distribute this
software is freely granted, provided that this notice
is preserved.
====================================================
```

## University of Illinois/NCSA Open Source License (ILGPU, ILGPU.Algorithms)

```
University of Illinois/NCSA Open Source License
Copyright (c) 2016-2025 ILGPU Project
All rights reserved.

Developed by:           Marcel Koester (m4rs@m4rs.net)
                        www.ilgpu.net

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal with
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

    * Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimers.

    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimers in the documentation
      and/or other materials provided with the distribution.

    * Neither the names of ILGPU, Marcel Koester, nor the names of its
      contributors may be used to endorse or promote products derived from this
      Software without specific prior written permission.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE CONTRIBUTORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS WITH THE SOFTWARE.
```

## BSD 3-Clause License (Snappier)

```
Copyright 2011-2020, Google, Inc. and Snappier Authors
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software without
   specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## MIT License (Parquet.Net, ZstdSharp.Port, K4os.Compression.LZ4, Microsoft.IO.RecyclableMemoryStream, CommunityToolkit.HighPerformance, .NET, Avalonia and its packages, MicroCom.Runtime, Tmds.DBus.Protocol, SkiaSharp, HarfBuzzSharp, CommunityToolkit.Mvvm)

Each of these components is distributed under the MIT License below, with the copyright line given for it in the
table above.

```
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

## BSD 3-Clause License (Skia, the native library in the SkiaSharp native-asset packages)

As SkiaSharp's THIRD-PARTY-NOTICES.txt reproduces it:

```
// Copyright (c) 2011 Google Inc. All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//    * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//    * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//    * Neither the name of Google Inc. nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## BSD 3-Clause License (ANGLE, in Avalonia.Angle.Windows.Natives)

The package's own LICENSE file:

```
// Copyright 2018 The ANGLE Project Authors.
// All rights reserved.
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions
// are met:
//
//     Redistributions of source code must retain the above copyright
//     notice, this list of conditions and the following disclaimer.
//
//     Redistributions in binary form must reproduce the above
//     copyright notice, this list of conditions and the following
//     disclaimer in the documentation and/or other materials provided
//     with the distribution.
//
//     Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc.
//     Ltd., nor the names of their contributors may be used to endorse
//     or promote products derived from this software without specific
//     prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
// FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
// COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
// INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
// BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
// LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
```

## "Old MIT" License (HarfBuzz, the native library in the HarfBuzzSharp native-asset packages)

As HarfBuzzSharp's THIRD-PARTY-NOTICES.txt reproduces it (the copyright sign is written "(C)" here, so this file stays
plain ASCII):

```
HarfBuzz is licensed under the so-called "Old MIT" license.  Details follow.
For parts of HarfBuzz that are licensed under different licenses see individual
files names COPYING in subdirectories where applicable.

Copyright (C) 2010,2011,2012  Google, Inc.
Copyright (C) 2012  Mozilla Foundation
Copyright (C) 2011  Codethink Limited
Copyright (C) 2008,2010  Nokia Corporation and/or its subsidiary(-ies)
Copyright (C) 2009  Keith Stribley
Copyright (C) 2009  Martin Hosken and SIL International
Copyright (C) 2007  Chris Wilson
Copyright (C) 2006  Behdad Esfahbod
Copyright (C) 2005  David Turner
Copyright (C) 2004,2007,2008,2009,2010  Red Hat, Inc.
Copyright (C) 1998-2004  David Turner and Werner Lemberg

For full copyright notices consult the individual files in the package.


Permission is hereby granted, without written agreement and without
license or royalty fees, to use, copy, modify, and distribute this
software and its documentation for any purpose, provided that the
above copyright notice and the following two paragraphs appear in
all copies of this software.

IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE TO ANY PARTY FOR
DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES
ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN
IF THE COPYRIGHT HOLDER HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH
DAMAGE.

THE COPYRIGHT HOLDER SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING,
BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE.  THE SOFTWARE PROVIDED HEREUNDER IS
ON AN "AS IS" BASIS, AND THE COPYRIGHT HOLDER HAS NO OBLIGATION TO
PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
```

## SIL Open Font License 1.1 (the Inter font, in Avalonia.Fonts.Inter)

The Inter project's LICENSE.txt for release 3.19 (the font's name table reads "Copyright 2020 The Inter Project
Authors (https://github.com/rsms/inter)", version 3.019):

```
Copyright (c) 2016-2020 The Inter Project Authors.
"Inter" is trademark of Rasmus Andersson.
https://github.com/rsms/inter

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL

-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION AND CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

## Bundled network data (attribution)

The command line tool ships the bundled cases under `data/` beside the executable. Two of the GB reference networks
in `data/matpower/gb` come from the Edinburgh Power Systems Test Case Archive and are redistributed with attribution
(N125, Ruling R1, 2026-10-04: kept bundled with attribution, as pandapower (BSD-3-Clause) and PyPSA-GB (MIT) already
redistribute them). Full provenance, SHA-256 checksums and terms are in
[`data/matpower/gb/README.md`](data/matpower/gb/README.md).

| File | Source | Attribution the source asks for | Terms |
|---|---|---|---|
| `data/matpower/gb/GBnetwork.m` (`gb-network-2224`) | W. A. Bukhsh and K. McKinnon, "GB Network", Power Systems Test Case Archive, University of Edinburgh, https://www.maths.ed.ac.uk/optenergy/NetworkData/fullGB/ ; the file names its data source as the GB SQSS review working group's publicly available data | Cite: W. A. Bukhsh, K. McKinnon, "Network data of real transmission networks", April 2013 | The archive states no licence; it says its contents are for research and educational purposes and are distributed with no guarantees regarding correctness, quality or fitness for a specific application |
| `data/matpower/gb/GBreducednetwork.m` (`gb-reduced-29`) | Reduced GB model, data by M. Belivanis (University of Strathclyde), MATPOWER file by W. A. Bukhsh (October 2012), as carried by PyPSA-GB | Cite M. Belivanis and the University of Strathclyde (the file's header), and the archive citation above | As above; the PyPSA-GB repository that carries this copy is MIT licensed (`data/matpower/gb/LICENSE-PyPSA-GB.txt`) |
| `data/matpower/gb/pypsa-gb/` (`pypsa-gb-reduced`, `pypsa-gb-zonal`) | PyPSA-GB (A. Lyden et al.), https://github.com/andrewlyden/PyPSA-GB | Copyright (c) 2022 Andrew Lyden | MIT (`data/matpower/gb/LICENSE-PyPSA-GB.txt`, kept beside the files) |

GridSim does not alter these files: the `.m` files are read as published (the PyPSA-GB copy of GBreducednetwork.m
differs from the archive original only by blank and comment lines; see the README). Published results computed from
them should carry the citations above.

### Every other bundled data source

The command line tool ships every folder below under `data/`; the web app and the desktop app ship `data/matpower`,
`data/gb-full`, `data/gb-synthetic` and `data/uk`. The licence and attribution files named here are shipped beside the
data they cover (the CLI ships all of them, the apps those of the folders they carry), and each folder's README gives
the full provenance. `BundledDataLicenceTests` fails when a listed licence file is missing from a build's output.

| Folder | Source | Terms | Shipped licence / attribution file |
|---|---|---|---|
| `data/matpower/lib`, `data/matpower/pegase`, `data/matpower/dist` | MATPOWER 8.0 `data/` (R. D. Zimmerman, C. E. Murillo-Sanchez et al.) | MATPOWER's code is BSD-3-Clause; its case files are not covered by that licence (in most cases included with permission or converted from public sources, as `LICENSE-MATPOWER.txt` states); PEGASE cases also ask for the citations in `data/matpower/README.md` | `data/matpower/LICENSE-MATPOWER.txt`, `data/matpower/README.md` |
| `data/matpower/rts`, `data/matpower/nesta` | PGLib-OPF (power-grid-lib/pglib-opf) | CC BY 4.0: attribution to PGLib-OPF, citation S. Babaeinejadsarookolaee et al., "The Power Grid Library for Benchmarking AC Optimal Power Flow Algorithms", arXiv:1908.02788 | `data/matpower/README.md` (attribution and citation) |
| `data/matpower/benchmarks/cigre_mv.m` | Authored by GridSim from the CIGRE Task Force C6.04.02 benchmark report (2014) following the pandapower reference `create_cigre_network_mv` (BSD-3-Clause) | Data transcribed from the published report; cite the CIGRE report | `data/matpower/README.md` |
| `data/matpower/gb` | Edinburgh archive and PyPSA-GB (table above) | As in the table above | `data/matpower/gb/LICENSE-PyPSA-GB.txt`, `data/matpower/gb/README.md` |
| `data/intl/ireland-all-island`, `data/intl/iberia`, `data/intl/france`, `data/intl/continental-europe` | ENTSO-E transmission map via GridKit and PyPSA-Eur, built into GridSim case folders | Geography (c) OpenStreetMap contributors, Open Database Licence (ODbL) 1.0; packaging PyPSA-Eur, CC BY 4.0. These maps are derived databases under the ODbL: they may be used, shared and adapted with attribution, and an adapted database that is shared publicly must be offered under the ODbL | each folder's `ATTRIBUTION.txt` and `MODEL_CARD.md`, and `data/intl/README.md` |
| `data/dist3ph` | IEEE PES Distribution System Analysis Subcommittee test feeders (IEEE 4 Node, IEEE 123 Node; W. H. Kersting), transcribed by GridSim | The published test-feeder data and results; cite the IEEE PES test feeders. The redistribution terms are not recorded in this repository and are to be confirmed before a public release | provenance in each case's `description` |
| `data/neso` | Published GB system-frequency recordings (NESO) | The source's terms are not recorded in this repository and are to be confirmed before a public release | this table |
| `data/gb-full`, `data/uk`, `data/gb-synthetic`, `data/events`, `data/devices`, `data/library` | Built by GridSim (`gb-synthetic` from no third-party data; `gb-full` and `uk` by the GDA builder from public data, see their READMEs) | GridSim's own licence (LICENSE.md) for what GridSim authored; see each README for any third-party source | `data/gb-full/README.md`, `data/gb-synthetic/README.md` |
