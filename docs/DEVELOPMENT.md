# 开发与构建

## 环境

- Windows 10 1809 或更新版本；新版资源管理器菜单验证需要 Windows 11。
- .NET 8 SDK。
- Visual Studio 2026、Windows App SDK C#、Windows SDK 和 MSVC v145 C++ 工具。
- 在 Visual Studio Installer 中导入 [`.vsconfig`](../.vsconfig)。ARM64 额外需要对应 C++ 工具。
- 注册开发 MSIX 包需要 Windows 允许开发部署。

依赖版本以项目文件为准。官方压缩引擎已随源码保留，无需另行安装 7-Zip 才能运行 AuraZip。

## 构建与运行

在仓库根目录执行：

```powershell
dotnet build AuraZip/AuraZip.csproj -p:Platform=x64 -p:Configuration=Debug
.\scripts\Run.ps1 -SkipBuild
# 或合并构建与启动
.\scripts\Run.ps1
```

`Run.ps1` 完成构建、本地 MSIX 注册和应用启动。重新构建前应先关闭正在运行的实例，以释放输出文件。

右键扩展随应用构建。`Build-ShellExtension.ps1` 使用 `vswhere` 定位 Visual Studio，从 `ArchiveFormats.json` 生成 `ArchiveFormats.g.h`，无需手动注册 DLL。

## 测试

```powershell
dotnet run --project tests/AuraZip.Core.Tests
dotnet run --project tests/AuraZip.Core.Tests -- --nested-only
dotnet run --project tests/AuraZip.Core.Tests -- --office-only
dotnet run --project tests/AuraZip.Core.Tests -- --office-process-only
dotnet run --project tests/AuraZip.Core.Tests -- --legacy-office-only
dotnet run --project tests/AuraZip.Core.Tests -- --office-layout-only
```

RAR 写入测试需单独安装或准备合法使用的 WinRAR 命令行编码器。编码器不属于仓库或发行包；`AURAZIP_TEST_RAR_ENCODER` 仅供测试进程使用，应用从设置读取路径。

```powershell
$env:AURAZIP_TEST_RAR_ENCODER = 'C:\Program Files\WinRAR\Rar.exe'
dotnet run --project tests/AuraZip.Core.Tests -- --rar-only
dotnet run --project tests/AuraZip.Core.Tests
powershell -File tests/RarUi.ps1 -EncoderPath $env:AURAZIP_TEST_RAR_ENCODER
```

核心测试生成独立临时目录，覆盖归档读取、解压、加密、分卷、CRC、路径检查、取消、元数据、ZIP/7Z 修改、嵌套写回与回滚、注释、哈希，以及 Office 文本、版式和解析进程限制。加密读取回归覆盖 RAR4/RAR5 与分卷、密码错误分类、ZIP AES 认证数据损坏，以及失败时保留原文件和清理临时目录。RAR 样本来源与许可见 [样本说明](../tests/Fixtures/README.md)。

先构建并注册 Debug 包，再运行界面验证。验证需要可交互桌面，避免同时操作测试窗口。

```powershell
powershell -File tests/CompressionUi.ps1 -Configuration Debug -IncludeTar
powershell -File tests/CompressionUi.ps1 -Configuration Debug -IncludeTar -IncludeStreams
powershell -File tests/FeaturesUi.ps1
powershell -File tests/FeaturesUi.ps1 -ArchiveFormat 7z
powershell -File tests/ProductivityUi.ps1
powershell -File tests/OfficeUi.ps1
powershell -File tests/OfficeUi.ps1 -EncryptedArchive
powershell -File tests/ProductivityUi.ps1 -ContainingFormat zip
powershell -File tests/ProductivityUi.ps1 -ContainingFormat 7z
powershell -File tests/PasswordUi.ps1
powershell -File tests/LargeArchiveUi.ps1

# 生成公共样本后验证导航、解压、语言和拖出
powershell -File scripts/New-SampleArchive.ps1
.\scripts\Run.ps1 -SkipBuild -ArchivePath .\artifacts\Samples\AuraZip-sample.zip
powershell -File tests/UiSmoke.ps1 -SampleArchive .\artifacts\Samples\AuraZip-sample.zip
powershell -File tests/NavigationUi.ps1
powershell -File tests/FluentUi.ps1
powershell -File tests/LanguageUi.ps1
powershell -File tests/DragUi.ps1
powershell -File tests/DragUi.ps1 -Multiple
```

`ProductivityUi.ps1` 使用系统默认文本编辑器验证文件写回流程。界面测试使用独立样本和输出目录。

`OfficeUi.ps1` 覆盖旧版文档、文档密码、原生版式控件、分页与取消；`-EncryptedArchive` 验证压缩包密码和文档密码互不影响。Office 解析使用同一可执行文件的独立进程，以匿名管道传输文档与密码，Windows Job Object 限制内存与子进程数量。密码不进入命令行或临时文件。不要并发运行会重建同一核心测试程序的脚本。

`LargeArchiveUi.ps1` 默认生成 10,000 个文件的 ZIP，记录界面打开、搜索和恢复列表的耗时及进程工作集。已构建的独立测试应用可设置 `AURAZIP_TEST_EXECUTABLE`，供采用 `UiActivation.ps1` 的测试脚本使用；未设置时仍使用开发包激活。

性能基准与核心测试分开运行，避免同时进行磁盘密集任务影响测量：

```powershell
dotnet run --project tests/AuraZip.Performance.Tests -c Release
```

基准覆盖 40,000 条目的目录查询与选择、128 MiB 文件的预览和校验，以及 5,000 个小文件的读取、解压和 ZIP 修改。耗时、累计托管内存分配和测试样本写入 `artifacts/Performance-<时间>/`。内存分配总量不等于进程峰值内存；比较结果时应保持机器、构建配置和后台负载一致。

`FluentUi.ps1` 检查导航与文件列表的无障碍名称、窄窗口预览的显示和隐藏、宽窗口布局恢复以及浅色与深色主题切换。截图写入 `artifacts/FluentUi-<标识>/`，测试结束后恢复窗口尺寸与主题。可用 `-AppProcessId <进程 ID>` 指定已打开公共样本的应用实例。

原生菜单测试位于 `tests/ShellTests.cpp`。用 Visual Studio MSBuild 构建 `AuraZip.Shell/AuraZip.Shell.vcxproj`，附加 `/p:Platform=x64 /p:Configuration=Debug /p:ShellTests=true`。运行生成的测试 EXE 时传入扩展 DLL 和实际压缩包的绝对路径；附加 `--registered` 检查已部署的 COM 注册。

## 商店打包

在 Visual Studio 中选择 Release 配置，右键应用项目，进入 Package and Publish，选择已关联的 Microsoft Store 应用。项目会生成用于提交的 `.msixupload`；多架构包选择 x86 和 x64，包捆绑选择 Always。中性架构不适用于包含原生代码的应用。

也可使用脚本生成商店提交包：

```powershell
powershell -File scripts/Build-Store.ps1
# 安装 ARM64 工具后可选
powershell -File scripts/Build-Store.ps1 -IncludeArm64
```

生成 x86/x64 的 `.msixupload`，内部包含 `.msixbundle`，输出至 `artifacts/Store-<清单版本>/`。商店发布使用项目清单中的应用身份，派生应用应配置自己的发布身份。

先生成公共样本和商店提交包，再验证真实包内容：

```powershell
powershell -File tests/StorePackage.ps1
powershell -File tests/CompressionUi.ps1 -UseRegisteredPackage -IncludeTar
```

`StorePackage.ps1` 从提交包中提取应用到独立目录，验证实际包内容与启动行为。

## 引擎、图标与本地目录

`scripts/Fetch-ArchiveEngine.ps1` 从官方固定版本获取引擎并校验 SHA-256。该维护脚本需要本机安装 7-Zip 以解包安装程序，应用自身无需依赖它。

`scripts/Generate-IconAssets.ps1` 从 `AuraZip/Assets/Brand/AuraZip-icon-fluent-v3.png` 生成窗口、任务栏、文件关联及 MSIX 清单使用的多尺寸 PNG 和 ICO 图标。更新图标源图后运行该脚本，并提交源图及生成的资源。

`bin/`、`obj/`、`artifacts/`、`AppPackages/`、`BundleArtifacts/`、`.vs/` 由 `.gitignore` 排除。`AuraZip/Engine/`、第三方许可证及对应源码随项目保留，并参与应用分发。

## 许可证

AuraZip 采用 [GPL-3.0-or-later](../LICENSE)。贡献与分发遵循同一许可证，第三方组件遵循其各自条款，详见 [第三方组件说明](THIRD_PARTY.md)。
