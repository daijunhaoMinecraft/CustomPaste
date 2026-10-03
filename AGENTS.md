# AI 开发指南

本文件适用于整个仓库，帮助 AI 定位代码、理解约束并验证修改。用户使用说明见 `README.md`；涉及具体行为时，以当前实现和测试为准，修改行为后同步更新文档。

## 项目概览

CustomPaste 是 Windows 托盘常驻的自定义粘贴工具：全局快捷键触发纯文本粘贴、翻译、模拟逐字输入或 AI 实时翻译输入，并提供历史与诊断日志。

- 解决方案：`CustomPaste.sln`。
- 主程序：`CustomPaste/CustomPaste.csproj`，`net8.0-windows`、WPF，启用 Windows Forms 以使用托盘组件。
- UI 依赖：`iNKORE.UI.WPF` 1.2.8、`iNKORE.UI.WPF.Modern` 0.10.2.1。不要假定其他版本的控件 API 可用。
- 主程序启用 nullable、禁用隐式 using；公共 using 见 `CustomPaste/GlobalUsings.cs`。
- 测试项目是自定义控制台断言程序，不是 xUnit / NUnit / MSTest；通过 `dotnet run` 执行。
- 构建和运行验证使用 Windows 与 .NET 8 或更新 SDK；运行应用需要 .NET 8 Desktop Runtime。WPF、DPAPI 和 Win32 行为不能用跨平台环境等价验证。
- 界面及面向用户的说明以简体中文为主，代码标识符沿用英文。

## 建议阅读顺序与代码地图

先看 `README.md` 的功能和安全限制，再按 `App.xaml.cs` → `AppSettings.cs` → `PasteService.cs` → 平台或翻译实现的顺序阅读。UI 相关工作再看 `SettingsWindow` 和自动保存逻辑。

以下路径相对仓库根目录：

| 文件 | 职责 / 修改时关注点 |
| --- | --- |
| `CustomPaste/App.xaml`、`App.xaml.cs` | 资源、启动与服务装配、单实例、托盘、快捷键分派、设置应用和退出生命周期。关闭设置窗口不等于退出程序。 |
| `CustomPaste/Models/AppSettings.cs` | 可观察设置、默认值、克隆、校验、翻译服务 / 粘贴动作枚举、语言列表与快捷键映射。新增配置首先检查这里。 |
| `CustomPaste/Models/Entries.cs` | 历史与日志条目的数据结构。 |
| `CustomPaste/SettingsWindow.xaml`、`.xaml.cs` | 六页设置界面、草稿编辑、校验与保存、翻译测试、历史 / 日志交互、主题。当前采用代码后置，不要假设存在独立 ViewModel 层。 |
| `CustomPaste/Services/SettingsAutoSave.cs` | DispatcherTimer 驱动的约 500 ms 防抖、待保存状态及恢复调度；与窗口中的草稿处理一起阅读。 |
| `CustomPaste/Services/PasteService.cs` | 粘贴编排：忙碌保护、读取文本、模式路由、翻译、输入、取消、状态、成功历史及日志。 |
| `CustomPaste/Services/WindowsPastePlatform.cs` | `IPastePlatform` 接口及 Windows 实现；前台目标校验、等待修饰键释放、Esc 监听、Unicode 模拟输入、剪贴板粘贴。测试通过注入平台替身避免真实输入。 |
| `CustomPaste/Services/ClipboardService.cs` | 剪贴板访问重试、多格式快照及恢复相关逻辑。与平台层合读。 |
| `CustomPaste/Services/NativeMethods.cs` | Win32 P/Invoke 与输入相关底层辅助代码。 |
| `CustomPaste/Services/HotkeyService.cs` | 快捷键解析、规范化、注册与冲突处理。 |
| `CustomPaste/Services/TranslationService.cs` | 翻译服务统一入口，DeepL、Microsoft、DeepLX 与 AI 非流式请求、超时和错误处理。 |
| `CustomPaste/Services/AITranslationProtocol.cs` | OpenAI 兼容 Chat Completions 地址、请求与完整响应校验。 |
| `CustomPaste/Services/AIStreamTranslation.cs` | `TranslationService` 的 partial 实现、SSE 状态校验和跨片段文本缓冲。 |
| `CustomPaste/Services/StorageService.cs` | 设置读写、原子保存、损坏恢复、DPAPI 加解密及数据目录。 |
| `CustomPaste/Services/HistoryService.cs` | 成功文本历史、数量限制、删除 / 清空及加密持久化。 |
| `CustomPaste/Services/LogService.cs` | UI 日志及 JSONL 文件、筛选所需数据、轮转与保留策略。 |
| `CustomPaste.Tests/Program.cs` | 离线测试入口、断言及测试替身；包含设置、存储、翻译协议和粘贴编排回归测试。 |
| `CustomPaste.Tests/StreamingTests.cs` | 自动保存、AI 流式协议与实时输入等回归测试；由测试程序执行。 |
| `CustomPaste.Tests/UiRender.cs` | WPF 离屏页面渲染和绑定诊断入口。 |

`bin/`、`obj/` 是生成目录，`artifacts/` 用于验证产物；不要通过修改这些目录修复源代码问题。

## 关键流程和不可随意破坏的约束

### 粘贴与输入

1. `App` 收到全局快捷键后交由 `PasteService` 编排；通过 `IPastePlatform` 访问桌面。
2. 读取剪贴板并校验文本长度，根据动作与设置选择翻译、普通逐字输入或纯文本粘贴。
3. AI 实时模式要求启用翻译、选择 AI 服务并启用 `AIStreamEnabled`，优先于普通流式输入设置；收到内容片段后即时模拟输入，不走剪贴板，也不额外等待逐字间隔。
4. 仅在整个任务成功后记录最终文本历史；失败、取消或流式部分结果不能伪装成成功。

必须保留：禁止并发任务、Esc / 全局取消传播、输入前等待快捷键释放、前台顶层窗口变化时停止、长度限制和资源释放。取消不撤回已发送文本；同一顶层窗口内焦点变化无法可靠检测。Enter / Tab 可能发送消息或切换字段，不能把真实桌面输入作为自动测试。

剪贴板恢复不能覆盖用户在等待期间新复制的内容；修改快照、恢复时机或序列检查后需补回归并进行手动验收。应用不主动提升权限，不能绕过 UIPI。

### 翻译协议

- 注入 `HttpClient`，用伪 HTTP handler 测试协议，不使用真实账户或计费接口作为默认验证。
- 保留取消、超时、响应体 / 文本长度限制和安全错误提示；不自动重试计费请求。
- `App` 创建的 HTTP handler 禁止自动重定向，避免密钥被转发；不要无意恢复默认重定向行为。
- AI 使用 Chat Completions 兼容协议，不是 Responses 或其他服务的原生协议。模型 ID 由用户填写，不自动替换模型。
- 翻译规则放 system 消息，待翻译文本单独放 user 消息，不执行工具，不发送历史对话。
- 非流式完整响应要求 `finish_reason: stop`；流式成功要求最终 `stop` 和 `[DONE]`。只输入 `delta.content`，不输入思考内容、拒答内容或工具参数。
- 断流、截断、拒答、过滤、工具调用或空结果不能回退粘贴原文。保留 UTF-8 网络分块、跨片段 CRLF 与字符缓冲处理。
- 自定义远程接口要求 HTTPS，本机回环地址例外；修改 URL 规范化时同步检查配置校验与协议测试。

### 设置、数据与隐私

- UI 编辑的是草稿；无效配置不能覆盖已生效设置。粘贴忙碌时延后保存，任务结束后处理待保存状态。
- 关闭历史需要确认清空；翻译示例文本、历史搜索和日志筛选不是持久化配置。
- 新增配置需同时检查：默认值、`PropertyChanged`、克隆与序列化、校验、UI 绑定、自动保存、运行时应用和旧配置加载兼容性。
- 默认数据在 `%LOCALAPPDATA%\CustomPaste`：`settings.json`、`history.dat`、`Logs\YYYY-MM-DD.jsonl`。开发和测试不要删除或改写用户现有数据，应使用隔离的测试目录。
- 密钥、自定义 DeepLX / AI 地址使用 Windows DPAPI CurrentUser；历史整体加密。模型 ID 和额外翻译要求并非加密字段，不应存放秘密。
- 日志不得包含剪贴板 / 译文正文、API Key、含令牌的完整 URL 或服务商响应体。避免直接记录可能携带上述内容的异常消息。
- 保留原子写入、损坏配置备份、历史裁剪与日志轮转；DPAPI 不提供对同一 Windows 用户下恶意程序的防护。

## 修改与验证工作流

保持改动聚焦，遵循邻近代码的命名和格式；不为小功能引入新 UI 架构、测试框架或无关依赖升级。注意 WPF Dispatcher / STA 线程要求，避免同步阻塞异步操作。不要顺带覆盖用户未提交的修改。

在仓库根目录执行：

```powershell
dotnet build CustomPaste.sln -c Release
dotnet run --project CustomPaste.Tests/CustomPaste.Tests.csproj -c Release
```

第二条才会实际运行自定义回归断言，不要把 `dotnet test` 没报错当作测试通过。新增用例沿用 `Program.cs` / `StreamingTests.cs` 中的测试组织方式，并确保被入口调用。输入逻辑用 `IPastePlatform` 替身；协议逻辑用模拟 HTTP 响应；持久化逻辑使用临时目录。

UI 修改还应执行并检查生成图片和绑定诊断：

```powershell
dotnet run --project CustomPaste.Tests/CustomPaste.Tests.csproj -c Release -- --render-ui artifacts/ui
```

离屏渲染不等价于真实桌面验收。托盘、系统快捷键冲突、剪贴板多格式恢复、权限差异、DPI、系统材质与真实服务商兼容性，按 `README.md` 的手动验收清单检查；涉及真实输入或计费请求前先取得用户同意。

交付时说明改动、执行过的验证及未验证项。仅文档变更通常不需要构建，但需核对路径、命令和链接；不能宣称未执行的测试通过。

## 许可证

`LICENSE` 是自定义 **NON-COMMERCIAL & SHARE-ALIKE LICENSE**，不是 MIT、Apache 或标准 Creative Commons 许可证。保留版权与许可证文本；不要替换许可证或把项目描述成允许无条件商业使用。README 徽章应链接到仓库内的 `LICENSE`，具体授权条件以原文为准。
