# 第三方组件

AuraZip 自有代码与文档采用 [GNU GPL v3.0 or later](../LICENSE)。以下第三方组件保留原有版权声明和许可证。

## SharpCompress

- 用途：归档读取、解压和部分 ZIP 写入。
- 来源：[SharpCompress](https://github.com/adamhathcock/sharpcompress)，版本以 `AuraZip.Core/AuraZip.Core.csproj` 为准。
- 许可：MIT，随应用保存于 [`AuraZip/ThirdParty/SharpCompress/License.txt`](../AuraZip/ThirdParty/SharpCompress/License.txt)。
- RAR 测试样本的来源及许可见 [`tests/Fixtures/README.md`](../tests/Fixtures/README.md)。

## 7-Zip

- 用途：作为外部进程创建压缩包，并完成加密归档解压、预览及完整性和认证校验。
- 来源：[官方 7-Zip 26.05 发布](https://github.com/ip7z/7zip/releases/tag/26.05)。
- 未修改的 x86、x64、ARM64 引擎位于 `AuraZip/Engine/`。
- 许可证、LGPL 文本和该版本对应源码位于 [`AuraZip/ThirdParty/7zip/`](../AuraZip/ThirdParty/7zip/)。
- 固定版本安装文件的 SHA-256 见 [`引擎说明`](../AuraZip/ThirdParty/7zip/README.md)，获取脚本同样校验这些摘要。

7-Zip 分发条款包含 LGPL、BSD 声明与 unRAR 限制，应保留完整许可证与对应源码。应用打包时也会包含这些文件。

## Office 预览

- 用途：Office 文档解密、OLE 容器与旧版 XLS 缓存单元格值读取。DOC/PPT 当前正文和编辑记录由 AuraZip 按微软公开格式规范读取。
- 组件、来源、固定版本与许可证见 [`Office 预览依赖声明`](../AuraZip/ThirdParty/OfficePreview/NOTICE.md)，许可文本随应用提供。
- NPOI 使用 Apache-2.0 许可的 2.7.6 版本。预览不执行宏、不计算公式、不访问外部链接。
- ImageSharp 与 NSax 不参与构建或运行时分发，构建检查会拒绝引入其运行时文件。

## Microsoft SDK 与运行时

WinUI、Windows App SDK、Windows SDK 及相关 NuGet 组件遵循各自许可，依赖与版本见项目文件。

项目的 GPL 许可证不改变第三方组件的许可条款。
