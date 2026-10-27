# Third-party notices

Typeless is MIT licensed (see `LICENSE`). The installer also ships the following components, unmodified, under their own licenses.

| Component | Version | License | Source |
|---|---|---|---|
| .NET Runtime and Windows Desktop Runtime (WPF, Windows Forms) | 8.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf, https://github.com/dotnet/winforms |
| C#/WinRT runtime and Windows SDK projection (`WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.dll`) | 2.x / 10.0.19041 | MIT | https://github.com/microsoft/CsWinRT |
| Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions | 8.0 | MIT | https://github.com/dotnet/runtime |
| System.IO.Packaging, System.Security.Cryptography.Pkcs | 8.0.1 | MIT | https://github.com/dotnet/runtime |
| DocumentFormat.OpenXml, DocumentFormat.OpenXml.Framework (Open XML SDK) | 3.5.1 | MIT | https://github.com/dotnet/Open-XML-SDK |
| PDFsharp | 6.2.4 | MIT | https://github.com/empira/PDFsharp |

The .NET runtime includes components from other projects; their notices are in the runtime repositories' `THIRD-PARTY-NOTICES.TXT` files linked above.

Typeless does not ship ffmpeg, LibreOffice or Microsoft Office. It uses them only when they are already installed. The installer can optionally install ffmpeg (`Gyan.FFmpeg`) through winget, which downloads it from its publisher under its own license (GPL).

## MIT License (applies to every component above)

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

Copyright holders: .NET Foundation and Contributors (.NET, Open XML SDK); Microsoft Corporation (C#/WinRT); empira Software GmbH (PDFsharp).
