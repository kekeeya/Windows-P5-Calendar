# Windows-P5-Calendar

Windows 桌面小工具，C# / .NET 8。

| 功能 | 说明 |
|---|---|
| 🗓 **桌面日历** | Persona 5 风格的日期贴纸：月/日、星期、天气、时段，天气图标三帧循环。可拖、可缩放、可关掉置顶；透明的地方点击穿透到下层窗口 |
| 🐈 **托盘图标** | 摩尔加纳的头像，按任务栏深浅自动反色 |
| ⚙️ **设置** | 日历显示/大小/置顶、天气城市（本地搜索三万四千条）、开机自启 |

---

## 运行

需要 Windows 10 1809 以上。**从 [Releases](../../releases) 下载 zip**，解压到固定目录，双击 `Mona.exe`。

- 第一次会被 **SmartScreen** 拦（没有代码签名）：「更多信息」→「仍要运行」
- **别在压缩软件里直接双击运行**，那是临时目录
- **别把 `Mona.exe` 单独拖出来**，`assets\` 和 5 个 dll 必须在旁边

启动后没有窗口也没有任务栏按钮，只有托盘图标——而且 Windows 默认把新图标折叠进 `^` 溢出区，需要拖出来固定。

**日历默认不显示**，左键点托盘图标才出现，之后记住选择。右键是「显示/隐藏日历」「设置…」「退出」三项菜单。

ARM 机器直接跑 x64 包（系统模拟），或用 `tools/publish.sh win-arm64` 重打。

---

## 代码结构

### 什么进了 exe

```
src/Mona.App   ──┐
                 ├─→ Mona.exe   （+ .NET 运行时 + Mona.ico 作为文件图标）
src/Mona.Core  ──┘

src/Mona.Tools ──→ Mona.ico     （换美术时重新生成，本身不进 exe）
assets/        ──→ 复制到 exe 旁边，运行时读取
```

`Mona.exe` 是自包含单文件：`Mona.App` 和 `Mona.Core` 编译成的 dll 打包在里面，还有整个 .NET 运行时——59 MB 里绝大部分是运行时，自己的代码只有几百 KB。

`assets/` **不在** exe 里（`Mona.ico` 除外），必须跟着 exe 一起分发。

### 三个项目

| 项目 | 目标框架 | 做什么 |
|---|---|---|
| `Mona.Core` | `net8.0` | 日历渲染、天气、城市表、配置、图像。**零第三方依赖，不碰 Windows API** |
| `Mona.App` | `net8.0-windows` | 托盘、分层窗口、设置窗、开机自启 |
| `Mona.Tools` | `net8.0` | 换素材时的工具，不参与运行 |

`Mona.Core` 刻意不依赖平台：日历的正确性是视觉的，把渲染留在纯托管代码里，就能在任何机器上出图对照，而不必先有一台 Windows。

### 文件一览

**Mona.Core**

| 文件 | 做什么 |
|---|---|
| `Calendar/CalendarLayout.cs` | `cal-layout.json` 的模型：每块美术的仿射矩阵、五层堆叠表、桥接半径、封缝笔宽 |
| `Calendar/CalendarArt.cs` | 按扁平名字加载并缓存 PNG，缺件是常态（月份十位就没有） |
| `Calendar/CalendarContent.cs` | 日期/星期/天气/时段的取值和 WMO 天气码映射 |
| `Calendar/CalendarRaster.cs` | 覆盖度场 + 形态学：洪泛补洞、连通块计数、精确圆盘闭运算、方框闭运算、凸四边形填充 |
| `Calendar/CalendarRenderer.cs` | 五层合成的主流程 |
| `Imaging/Sampler.cs` | 仿射重采样，帐篷核，目标空间预滤波 |
| `Imaging/Png.cs` | PNG 编解码（8 位，非隔行） |
| `Imaging/Ico.cs` | 多尺寸 ICO 写入 + 圆角底板合成 |
| `Imaging/TrayFrame.cs` | 按 alpha 包围盒裁剪后盒式缩放到托盘尺寸 |
| `Settings/Preferences.cs` | `%APPDATA%\Mona\settings.json`，整份原子写入 |
| `Settings/Cities.cs` | 城市短名单 + 三万四千条 TSV 的本地搜索 |
| `Weather/WeatherSource.cs` | Open-Meteo，半小时一次，失败就沿用上次 |
| `Diagnostics/Log.cs` | `%APPDATA%\Mona\log.txt` |

**Mona.App**

| 文件 | 做什么 |
|---|---|
| `Program.cs` | 入口：单实例互斥、日志、未捕获异常兜底、DPI 模式 |
| `MonaContext.cs` | 没有主窗口的 `ApplicationContext`：托盘、菜单、各部件接线 |
| `CalendarWindow.cs` | 分层窗口，逐像素 alpha、拖动、DPI 变化、三帧循环 |
| `Tray/IconFactory.cs` | 剪影染色成托盘图标，管理 HICON 生命周期 |
| `SettingsForm.cs` | 设置窗，`TableLayoutPanel` 自动布局 |
| `LaunchAtLogin.cs` | `HKCU\...\Run` |
| `AppPaths.cs` | 找 `assets/`：先看 exe 旁边，再向上找 |
| `Native/Win32.cs` | 用到的全部 P/Invoke，集中一处 |

### 日历是怎么画出来的

一张贴纸的流程：

1. **查表**。用「月-日」「星期」「时段」「天气+帧」四个键从 `cal-layout.json` 取出每块美术的位置。表里没有的键就不画——一位数的月份没有十位，这是正常的
2. **逐层光栅化**。五层从下到上：白底 → 天气图标 → 黑底 → 白字 → 文字。每块美术只读 alpha，因为**颜色是层的属性而不是件的属性**
3. **层内并集用 `min(1, a+b)`**，不是 `1-(1-a)(1-b)`。两块白只是挨着时各覆盖半个边界像素，乘性并集算出 0.75，接缝处就留一条四分之一透明的发丝线，压在壁纸上是一道灰痕
4. **黑层补洞**。月/日/斜杠/星期四块黑在设计里是一整片，它们圈住的白都该是黑的。做法是从画布边缘洪泛，淹不到的就是内部。四块黑没连上时先用圆盘闭运算桥接，**但只在真的连起来时才采纳**——只是把一块变胖不算桥接
5. **封发丝缝**。相邻底板之间会留一两像素的缝，用方框闭运算封掉。只在有底板的层做，文字层不能做，否则汉字里两三像素宽的白槽会被焊死
6. **预乘还原**。累积出来的颜色是预乘的，PNG 要的是直通 alpha。少这一步整张贴纸会镶一圈灰边——白描边的抗锯齿边缘覆盖度是二分之一，直接当直通存就成了半透明的中灰而不是半透明的白

天气三帧一次全画好再循环：只有最下面两层跟天气有关，上面三层三帧完全相同，而形态学的开销全在上面三层，所以共用。整个渲染在后台线程，主线程只负责换图。

### 托盘图标

Windows 的托盘图标就是一张位图，系统不会替你适配任务栏深浅。所以程序读注册表 `SystemUsesLightTheme`，把剪影染成黑或白——不染的话黑图标在默认深色任务栏上是隐形的，看起来就像没启动。

图标用 `CreateIconIndirect` + 32 位 DIB 生成，而不是 `Bitmap.GetHicon()`：后者一行就够，但它有把 alpha 压成 1 位掩码的历史，而这是一张十六像素的抗锯齿剪影，压完就只剩锯齿。

HICON 不是垃圾回收管的东西，**旧的一代要晚一代再销毁**：`Shell_NotifyIcon` 持有的是句柄本身而不是副本，新图标刚建好就销毁旧的，托盘会闪一下空白。

### 日历窗口

分层窗口（`UpdateLayeredWindow`），不是 WinForms 的透明窗口。这不是性能选择——它是顶层窗口拿到**真正逐像素 alpha** 的唯一办法，而且**点击穿透是白送的**：Windows 按窗口自身的 alpha 路由鼠标消息，斜贴纸的透明四角自动落到下层窗口，代码里一个像素都不用测。

拖动来自 `WM_NCHITTEST` 返回 `HTCAPTION`：每个不透明像素都是标题栏。同时吞掉 `WM_NCRBUTTONDOWN/UP`，否则右键会弹出系统菜单——对一个无边框贴纸来说那是个空盒子。

缓冲区交给 `UpdateLayeredWindow` 前必须**预乘**，直通 alpha 会让每条抗锯齿边缘变成亮边。

### 美术资源

```
assets/CalendarArt/    cal-layout.json、cal-cities.tsv、113 张 PNG
assets/Tray/           MonaHead.png
```

```
assets/Tray/Mona.ico   由 MonaHead.png 生成，嵌进 exe 当文件图标
```

换托盘图标要求是**单色剪影**：形状在 alpha 通道里，不透明部分色调统一（黑白都行，程序会按任务栏反色）。换完走这三步：

```bash
dotnet run --project src/Mona.Tools -- inspect assets/Tray/MonaHead.png   # 能不能安全染色
dotnet run --project src/Mona.Tools -- tray                               # 16/20/24/32 px 对照图
dotnet run --project src/Mona.Tools -- icon                               # 重新生成 Mona.ico + 预览
```

`icon` 必须跑，否则 exe 的文件图标还是旧的那张。缺资源时程序不崩，托盘会弹气泡说缺了什么。


---

## 版权说明

源代码以 **[MIT License](LICENSE)** 发布。

**美术资源不在此许可范围内。** 摩尔加纳及《女神异闻录 5》（Persona 5）相关的一切权利归 Atlus / Sega 所有，本项目不拥有也不授权这些权利。

**城市数据** © [GeoNames](https://www.geonames.org)，以 [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) 发布。

非商业粉丝作品，与 Atlus、Sega 无关联，不收费、不接受捐赠、不含广告。若权利方认为有任何不妥，请通过 issue 或邮件联系，我会立即下架相关内容。
