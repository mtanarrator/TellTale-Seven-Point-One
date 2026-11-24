# Third-Party Notices

TellTale 7-Point-One (VegasSurround71) is licensed under the Apache License 2.0.
That license applies to this repository's own source code. It does not relicense
the third-party platforms, tools, or codecs listed below.

## VEGAS Pro and ScriptPortal.Vegas

The extension is built for Boris FX / MAGIX VEGAS Pro and references the
proprietary `ScriptPortal.Vegas` SDK. The SDK and VEGAS Pro are not distributed
with this repository. Users must have a valid VEGAS Pro installation and comply
with its license terms.

## FFmpeg

FFmpeg is not bundled or distributed with this project. The extension invokes a
user-installed `ffmpeg` executable for final MKV muxing and HOA FLAC encoding.
FFmpeg builds can be licensed under LGPL-2.1-or-later or GPL-2.0-or-later,
depending on how they are configured, and may include codecs with separate
patent or licensing terms. Users are responsible for the FFmpeg build they
install and for complying with its license and any applicable codec terms.

## Audio codecs and containers

The extension delivers FLAC, Opus, Vorbis, WavPack, and LPCM audio in Matroska
(MKV), plus WAV/Wave64 PCM and AmbiX masters. FLAC, Opus, Vorbis, WavPack, and
LPCM are open, royalty-free codec technologies. MKV, WAV, and Wave64 are open or
simple containers. No AAC or E-AC-3 delivery path is implemented.

Codec implementations are provided by the user's FFmpeg build or by VEGAS Pro.
This repository does not vendor codec libraries.

## Video codecs

Video is rendered by VEGAS Pro and copied into the final MKV without re-encoding.
H.264 and HEVC can involve patent pools; AV1 is royalty-free; ProRes is Apple's
codec and is subject to Apple's terms. Video codec licensing is separate from
this project's Apache 2.0 license.

## Microsoft .NET

The C# panel targets .NET Framework 4.8 and uses the
`Microsoft.NETFramework.ReferenceAssemblies.net48` package at build time. That
package is distributed under the MIT license. The .NET Framework runtime is a
Microsoft platform component.

## Rust

The native core has no external crate dependencies. It is built as a Rust
`cdylib`/`rlib`; the Rust standard library and toolchain are subject to their
respective licenses.

## Tutorial

`tutorials/opengl_hoa/` is original project code. It uses the browser's WebGL 2
and Web Audio APIs and does not bundle external fonts, images, libraries, or
media assets. The screenshots in `artifacts/tutorial/` are project-generated.

## No legal advice

This notice is provided for attribution and practical clarity. It is not legal
advice. Consult qualified counsel for a definitive licensing or patent review of
your specific distribution and use case.
