# Read-only Unity metadata adapter

The Vendor directory contains the MIT-licensed binary/metadata parsers from
https://github.com/vmpprotect/Il2CppDumper/tree/24d8241b76ae79bbca92abc64aca00f66b3f11ce
(Perfare/Il2CppDumper, metadata 39 support PR #922). LICENSE is included.
Only parsing, format definitions and section discovery are included. No process
injection, executable loading, dump generation or game files are distributed.

The adapter resolves the UI module type-information slot and member offsets from
the installed game files. It never imports offsets from another store/build.
Unknown metadata/layouts fail closed. The current-screen reader is separate from
game data writers and does not grant write support for an unknown game build.
