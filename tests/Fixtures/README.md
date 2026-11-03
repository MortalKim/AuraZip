# RAR fixtures

`Rar.rar` and `Rar5.solid.rar` come from the SharpCompress test archive collection, pinned to commit `b6cc95af73950c914e0c6ce7ea3511528ede1121` (the source commit of the referenced NuGet package).

Source: https://github.com/adamhathcock/sharpcompress/tree/b6cc95af73950c914e0c6ce7ea3511528ede1121/tests/TestArchives/Archives

The upstream MIT license is included as `SharpCompress-LICENSE.txt`. These files are used only for archive decoding, extraction and checksum tests.

Multipart fixtures `Rar.multi.part*`, `Rar.multi.solid.part*` and `Rar2.multi.*` come from the same pinned commit and license. They cover normal, solid and legacy multipart RAR extraction.
