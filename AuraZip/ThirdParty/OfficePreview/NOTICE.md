# Office preview dependencies

Office document decryption, OLE container access and legacy XLS reading use NPOI 2.7.6 under Apache-2.0. AuraZip reads OOXML text and layout, legacy DOC piece tables and current PPT edit records with its own read-only parsers.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| NPOI | 2.7.6 | Apache-2.0 | https://github.com/nissl-lab/npoi/tree/a0f50a01a845aa4dd793cee0ac5b776745966cb6 |
| BouncyCastle.Cryptography | 2.6.2 | MIT | https://github.com/bcgit/bc-csharp |
| Enums.NET | 5.0.0 | MIT | https://github.com/TylerBrinkley/Enums.NET |
| ExtendedNumerics.BigDecimal | 2025.1001.2.129 | MIT | https://github.com/AdamWhiteHat/BigDecimal |
| MathNet.Numerics.Signed | 5.0.0 | MIT | https://github.com/mathnet/mathnet-numerics |
| Microsoft.IO.RecyclableMemoryStream | 3.0.1 | MIT | https://github.com/microsoft/Microsoft.IO.RecyclableMemoryStream |
| SharpZipLib | 1.4.2 | MIT | https://github.com/icsharpcode/SharpZipLib |
| SixLabors.Fonts | 1.0.1 | Apache-2.0 | https://github.com/SixLabors/Fonts |
| ZString | 2.6.0 | MIT | https://github.com/Cysharp/ZString |
| System.Security.Cryptography.Xml / Pkcs | 10.0.14 | MIT | https://github.com/dotnet/runtime |

License texts are included alongside this notice. Microsoft compatibility libraries follow the .NET license included here.

ImageSharp and NSax are excluded from compile and runtime assets and are not distributed with AuraZip. Office previews do not decode images or load SAX adapters through NPOI. A build guard rejects either excluded engine if it appears in the resolved runtime assets.
