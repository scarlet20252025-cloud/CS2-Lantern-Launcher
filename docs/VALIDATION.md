# 2026-10-21 v0.5.28 人数上限入口：说清「现在是什么、点了会怎样」

用户反馈：「修改人数上限的入口表达还是很不清晰」。v0.5.27 已经把输入框搬进独立卡片，但控件本身仍在误导。

## 三个误导点

1. **按钮常驻且置灰**。服务器没运行时也显示一个灰掉的「保存并重启服务器」，看起来像坏控件；实际上那种情况下根本没有进程可重启，输入的数字只是下次启动的参数。
2. **文案没说自己属于哪种状态**。三种完全不同的处境（没运行 / 一致 / 不一致）共用一句「启动后显示实际上限」，用户无法判断该不该动它。
3. **比较对象错了**（真 bug）。`RaiseCapacity` 用 `Settings().MaxPlayers`（保存的偏好）当当前值，而界面展示的是 `GameProcesses.Find(...).RequestedMaxPlayers`（运行中进程的启动参数）。改过数字但没重启时两者不一致，于是「提高到 16」被判成「当前上限已是 16，无需重启」直接抛异常——入口看着能点，点了永远失败。

## 修法

- 一律以**运行中进程的 `-maxplayers`** 为准，改高改低都允许；只有与进程完全相同才拒绝。
- 按钮按状态出现，而不是按状态置灰：没进程 / 数字一致 → `Visibility.Collapsed`；数字不同 → 显示，且文案带目标值「按 16 人重启服务器」。
- 每种状态各写一句能直接行动的话（见 README 的四种状态）。

## 证据

`--capacity-smoke` 现在同时断言入口的**可见性**与**可用性**，并截图 `capacity-entry.png` 复核首屏可读性：

```
PASS ceiling 12 -> 14 reached the new process; dedicated PID 27356 -> 34036;
     entry point disabled while values matched and enabled while they differed
```

断言链：数字与进程一致 → 按钮不 enabled；改成 target → 按钮 enabled 且可见、`CapacityState` 含目标值；执行后 → 按钮再次不可用；最后核对**新进程**的 `-maxplayers` 等于 target，且 PID 已变（证明真的重启了，而不是只改了界面）。

## 换图守卫的在线取证（补 v0.5.27 只做了离线的缺口）

v0.5.27 用真机抓下来的 `status` 块做离线回归。这次趁用户退出游戏，用启动器自己拉起的服务器做了只读与真实换图两种在线验证：

```
PASS guard agreed with clients_human; trace:
564ms humans=1 ready=0 [Mikuo:spawning]
1915ms humans=1 ready=0 [Mikuo:spawning]
3185ms humans=1 ready=0 [Mikuo:spawning]
4444ms humans=1 ready=1 [Mikuo:active]
```

```
PASS switched to de_dust2 while 1 human(s) were already active; guard no longer reports 0 ready
```

第二条是用户报的那个场景本身：真人已经站在地图里，仍然能换图。第一条顺带证明了 `spawning` 期间守卫会等待、不会误判，也不会靠固定睡眠蒙对。

# 2026-10-21 v0.5.27 换图守卫误判（真机 status 表结构变化）

用户报告：「我已经进入了地图，切换地图的时候还是显示这个」——弹窗写「玩家仍在连接或加载（已就绪 0/1）」。

## 根因：adr 列不再是 SteamID

从运行中的 1.41.9.1 独立服务器抓到真实 `status` 输出：

```
---------players--------
  id     time ping loss      state   rate adr name
   0    02:02    0    0     active 786432 127.0.0.1:50333 'Mikuo'
 769      BOT    0    0     active      0 'Comb'
65535 [NoChan]    0    0 challenging      0unknown ''
#end
```

同一时刻 `status_json` 报 `players : 1 humans, 9 bots`。

旧实现 `ActiveHumans` 用正则要求 `adr` 形如 `[U:\d+:\d+](:\d+)?`。这版 CS2 给的是纯 `ip:port`，正则一条都不匹配 → 数出 0 个已就绪真人 → 守卫比较 `active != before.Humans` 恒成立 → 每次换图都被拒绝，文案还让人「等他们进入地图」，而人早就在地图里了。

## 修法

按列位置解析，不依赖地址格式：

- 先切掉末尾的引号名字段（名字可以含空格，甚至含 `BOT`），再数剩余列。
- `state` 在第 5 列（索引 4）——三种行形态（真人 / 机器人 / 空槽）都一致。
- 机器人判据是「剩余列里出现裸 `BOT`」（新版在 time 列，旧版在 adr 列，都能命中）。
- 空槽 `65535 [NoChan] … challenging` 既非真人也不 ready。

## 证据

回归用例直接内嵌上面那段真机输出，断言 `ActiveHumans(block) == clients_human == 1`，并断言「完全加载的玩家不在 LoadingHumans 里」。另有反例用例：名字叫 `BOT Bob` 的真人必须计为真人。

`ALL 132 CHECKS PASSED`。

## 无法在线复验的部分

用户当时的服务器是**上一个启动器进程**拉起的，启动器退出后命名管道随之消失（`控制台管道已不存在`），所以不能在不动当前对局的前提下连上去读。本次用「真机抓取的历史输出 + 离线回归」取证，未重启用户服务器。下一次由启动器自己开服时，`--guard-smoke` 会做只读在线比对（不启动、不重启任何进程）。

# 2026-10-21 v0.5.26 加入指令改为 steam://run（steam://connect 被 Steam 拒绝）

用户实测报告：在 Steam 的「游戏服务器」窗口点连接会弹「失败 · 无法连接服务器，服务器指定的 app id 无效」。

## 先证明服务器和 frp 都没问题

用 A2S 查询协议在本地与公网入口各打 10 次：

```
LOCAL  A2S_INFO ok=10 fail=0 apps={730: 10} median=0ms
PUBLIC A2S_INFO ok=10 fail=0 apps={730: 10} median=23ms
```

`app_id` 返回的就是 730（CS2），公网入口原样透传。frp 配置也确认正确：`CS2-27016 udp 127.0.0.1:27016 -> 10003`。所以"app id 无效"是 Steam 客户端的误报，不是服务器拒绝。这是 Steam 自 2023-09 起的已知问题，触发点正是「游戏服务器窗口点连接」与 `steam://connect/...` 两条路径。

## 改法

`Hosting.SteamConnectUrl` 返回值从 `steam://connect/<addr>` 改为 `steam://run/730//+connect <addr>/`。

`steam://run` 让 Steam 直接带游戏参数启动 730，不经过它那条会搞混 app 的交接代码。这条在本机端到端验证通过：

```
PASS steam://run/730//+connect 127.0.0.1:27016/ pulled client PID 18796 into the server;
     humans 0 -> 1; map de_inferno_minecraft; port 27016
```

## 顺带

- "复制加入命令"改为复制同一条加入指令，不再只给 `connect ip:port` 的控制台命令；两个入口现在给的东西一致。
- "复制加入说明"不再附控制台备选，改为说明聊天软件不会把自定义协议变成可点击链接，要按 `Win+R` 或粘到浏览器。
- 按钮文案改为「复制加入指令（好友 Win+R 粘贴即进服）」，避免让人以为是可点击的聊天链接。


# 2026-10-21 v0.5.24 好友零安装进服（Steam 邀请链接）

起因：用户指出「不进入地图就不能通过 ~ 打开控制台加入游戏」，而要求朋友为了进服再装一个启动器并不合理。

## 结论

`steam://connect/<ip>:<port>` 可以让没装启动器、没打开控制台的玩家直接进服。这项能力已在本机端到端验证，不是只生成了一个字符串。

## 实测

```
PASS steam://connect/127.0.0.1:27016 pulled client PID 18796 into the server;
     humans 0 -> 1; map fruitninja; port 27016
```

`--invite-smoke` 的判定方式是读服务器自己的 `status_json`：先记录真人计数，打开链接，再轮询到真人计数确实增加，同时要求存在一个非独立模式的 cs2 进程。仅凭「打开链接没有报错」不算通过。

## 同时移除房间密码

`HostSettings.Password`、`sv_password` 写入、`password "…";` 前缀一并删除。理由：邀请链接无法携带密码，保留密码只会让好友多点一步。`JoinCommand`/`ReconnectCommand` 现在都只接受地址与端口。

## 已知边界

- 邀请链接只能携带 IP。Steam 服务器身份 `[A:…]` 无法放进链接，P2P 模式仍退回复制控制台命令。
- 本机用 `127.0.0.1` 验证，**好友从自己的网络能否连进来仍未实测**；这条从 v0.5 起一直未验证。
- 组网 IP（EasyTier / Radmin / ZeroTier）双方必须先在同一虚拟网络内。

## 测试

`ALL 124 CHECKS PASSED`（新增 4 条邀请链接用例，删除 1 条密码用例）。启动器自检 `--smoke --restart-smoke --terminal-smoke --tab-layout` 全部 PASS，独立服务器 PID 28748 -> 21412，客户端 PID 18796 保持不变。

# v0.5 验证（2026-10-12）

- Release 发布通过，56 项核心检查通过；新增订阅缓存、仅订阅变化的指纹检测、粘连链接批量解析、空 Steam P2P 身份拒绝。
- 用户提供的两张地图已在实际启动器中显示待下载；Steam 本地缓存当时未同步这两个 ID。安装盘空间不足的状况已显示，不会误标地图可启动。
- 新版主窗口通过实际 Windows UI 检查；选中地图操作、全局终端、联机入口分组，联机页隐藏换图按钮，增加文字字号和对比度。
- 真实 status_json 接口与联机 UI 通过。曾观察到空服务器身份，随后用户换图后有有效身份；空身份会拒绝生成加入命令。没有两个账号/跨网可达性验证，不声称保证好友可加入。
- 用户明确授权后运行《流放之路：降临》自带卸载程序；游戏安装目录已不存在。E 盘净空闲增加约 150.76 GiB。HKLM 卸载展示条目残留，精确删除该条目被 Windows 权限拒绝；未提升权限或修改其它注册表项。其它磁盘扫描为只读。

---

# v0.4 验证（2026-10-12）

- 发布构建通过，0 warnings / errors；核心 51 项检查通过。
- 3 对 4 → T 加 1、4v4；5 对 2 → CT 加 3、5v5；相等 / 空房 / 非法输入有测试。未使用 7 个真人账号实测 4v4，真实两队人数仍需手填。
- WPF 自检：新增夹具 VPK → 自动刷新 → 保留选择 → Windows 回收站移除夹具 → 自动移除条目，通过。没有删除用户的任何真实地图。
- 删除范围检查：官方地图、根外文件、ID 不匹配均拒绝；删除前验证链接 / 联接边界。
- 真实 CS2 验证：已认证 NetCon echo、status_json 地图 / 真人 / 人机读取通过；本轮没有重启游戏或更换当前地图。
- 发布版界面已渲染检查，主窗口、独立终端截图位于 artifacts/v04-smoke。原有地图选择回归也通过。
- Steam 打开地址已统一为协议 URI，未替用户进行订阅 / 取消订阅。实际 Steam 新下载全流程尚未实测；自动识别测试使用隔离下载夹具。

以下为早期版本记录，其“本次未启动游戏”等表述仅指该历史阶段。

---

# 本机验收记录

## v0.3 实机终端与双视图

在用户明确授权重启游戏后，以本机 CS2 1.41.9.0 实测。单独 `-vconsole` 未开放接口；加入私服 `-insecure` 后可开放。VConsole 二进制批量 CVRB 数据与最初参考的短包解析存在兼容差异，最终采用实测可用的带密码文本 NetCon，不在发布程序中启用 VConsole。

- 同一 GameConsole 客户端连接真实 CS2，验证随机 echo 回显。随后再带 `-netconpassword` 重启，密码连接同样通过。
- 删除：游戏 status 输出 `1 humans, 0 bots`，查询 `bot_quota = 0`。
- 补位：填 6，输出 `bot_quota_mode = fill`、`bot_quota = 6`、`mp_autoteambalance = true`、`mp_limitteams = 1`，status 输出 `1 humans, 5 bots`。
- 重开：游戏返回“离游戏重新开始还有 1 秒钟”，随后出现 OnPreResetRound / BeginMatch。
- 实际 TerminalWindow → 密码连接 → 发送 echo → 收到游戏原文 → 窗口日志，端到端检查通过。截图和记录位于 `artifacts/v03-final-smoke/terminal-smoke.png` 与 `.txt`。
- 地图选择经过元数据刷新、网格／列表双向切换后仍保持单一正确选择，启动命令仍对应 `crazy_chickens`。
- 核心检查 41 项通过；包含 UTF-8 命令编码、控制字符／长度拒绝、人机 quota 语义及之前回归。

实机控制输出在 `artifacts/console-live.txt`（本地诊断含玩家／网络状态，不随 EXE 分发）。`--console-live --key <console-key.txt>` 会实际删人机、补位和重开对局，只用于明确授权的游戏实测。地图网格实现为 WPF WrapPanel；列表保留虚拟化，封面限宽解码和缓存。

## v0.2 选图修复与外观更新

用户报告刷新名称／封面后点击 Inferno Crazy Chickens，实际启动仍为 Minecraft Aim。本机只读检查最新 boot.cfg 确认旧地图命令仍被生成。加入 WPF 界面回归后，旧版精确复现：卡片为 `crazy_chickens`，`SelectedItem` 仍指向旧地图，`SelectedItems.Count=2`，右侧标题也是旧地图。

原因是 `MapEntry` 使用带可变字段的 C# record，元数据更新改变其值哈希，破坏 WPF Selector 内部已选条目索引。v0.2 改为稳定引用身份的 class，跨扫描仍使用显式 Identity 找回选择；标题直接绑定当前 SelectedItem。修复后同一回归通过，得到 `map_workshop 3815559839 crazy_chickens`，单一选中项、标题、目标命令一致。

新增元数据变更后哈希集合仍能定位已选地图的核心检查，以及 `--smoke --selection-test` WPF 回归。该检查不启动游戏、不改真实 cfg；日志在 `artifacts/selection-after/selection-test.txt`。本次 35 项核心检查 + 1 项本机地图扫描通过；在线元数据另由界面封面获取验证。

外观统一深色控件与下拉框，选中地图有橙色竖线和“已选”标识，右侧增加内部名称／工坊 ID。EXE、窗口标题栏与应用头部采用同一灯笼图标，ICO 包含 16/24/32/48/64/128/256 像素规格，保留 SVG 和图标生成脚本。

游戏已运行时仍需将已复制的命令粘贴到 CS2 控制台；新增明确目标地图的命令窗口，避免把“已复制”误认为“已切换”。未向用户正在进行的游戏发送换图操作。

日期：2026-10-12。此记录区分启动器功能检查和实际游戏联网验收。

## 已通过

- Windows x64 WPF Release 构建：0 errors / 0 warnings。
- 便携发布：包含 .NET 10 运行时，单个可执行文件约 62 MiB；不包含 Steam、CS2 或地图内容。
- 36 项检查全部通过，输出在 `artifacts/test-results.txt`。
- Steam 多库检测找到本机实际安装目录和工坊目录。
- 真实数据扫描：**24 张工坊地图 + 23 张自带地图，共 47 张**。排除同包辅助 skybox 与官方 vanity 场景。最后一轮只读扫描 278 ms；结果会随安装内容变化。
- 读取 VPK v2 索引、preload、嵌套地图包、分卷完整性；损坏／缺失分卷不会阻断其余文件扫描。地图体积包含分卷。
- 在线 Steam API 返回本机 Cobblestone 地图名称和 Map / Cs2 标签，成功下载并缓存实际工坊封面。
- CS2 客户端二进制只读检查确认 `map_workshop <addonname> <map_name> ...` 语法。
- Listen / dedicated 配置生成、规则关闭、输入范围、控制台注入字符拒绝、IPv4 / IPv6 / 服务器 P2P 身份连接命令。
- P2P 修改与恢复在临时测试夹具中通过，保留原字段／注释、重复应用不重复插入、外部修改后拒绝旧备份恢复。
- 发布版 EXE 独立运行，完成本机扫描、封面缓存、自身 WPF 渲染并退出，exit code 0。实测界面图在 `artifacts/portable-smoke/ui-smoke.png`。

## 性能口径

本次扫描不解压地图、没有全盘遍历，读取的是 VPK 目录和文件属性。约 0.2–0.3 秒的扫描只是这台电脑、这些文件、已有系统文件缓存条件下的观测，不是全平台承诺。

便携版运行时自检指标保存在 `artifacts/portable-smoke/smoke.json`：`readyMs` 为进程进入托管代码后初始化与本地扫描的时间，不包括进程创建／单文件首次展开的全部时间；`readyWorkingSetMiB` 与 `renderWorkingSetMiB` 是阶段工作集快照，不能当作稳定空闲内存、峰值或与 Electron 的对照实验。界面截图本身会额外分配内存。

没有进行游戏 FPS、服务器 tick 耗时、SDR ping 或不同启动器的同条件性能对比。可在开图后关闭启动器，让其不再占用进程资源。

## 尚未通过端到端验收

- 从 Steam 启动 → 地图实际加载 → 规则最终生效。真实配置写入／游戏启动没有在本次检查中执行。
- 两个 Steam 账号通过 Open Party 加入同一局，以及外部 `map_workshop` 快捷启动是否保留可加入的大厅状态。
- 两台电脑 IP 直连、NAT／公网 UDP 可达性、不同网络下的 P2P 连接和中继延迟。
- 工坊页面点击订阅到 Steam 下载完成的 UI 全流程。已验证公开元数据 API 与已有地图扫描，不自动代替用户订阅。
- 自定义地图脚本对规则的覆盖、无导航网格地图的人机行为。

这些条件不能由静态命令生成检查替代。应用因此显示“启动请求已发送”，不会显示未经检测的“服务器已就绪”。P2P 始终标注实验，Open Party 保留官方 UI 回退流程。

本次开发未修改真实 CS2 `gameinfo`、`autoexec` 或服务器配置，未开游戏、改防火墙或执行订阅。修改仅发生在项目目录、开发工具链与测试数据目录。

## 2026-10-13 v0.5.18 重启崩溃恢复

- 实际复现 Redline Minecraft 3648210230 在独立服务器加载时触发 FATAL ERROR：maps/prefabs/misc/end_of_match.vpk Failed to load file (invalid)。改变工作目录仍复现；未修复或改写工坊包。
- 目标图加载使已跟踪服务器退出时，保存 NetCon 尾部诊断，最多自动恢复一次到官方 Dust II。核验实际地图和服务器身份后才显示恢复成功；UI 同步为恢复地图和规则，防止下次重启继续载入崩溃地图。失败则停止自动尝试。
- 实测：Redline 引擎退出 -> Dust II 恢复 -> 正常重启；客户端 PID 33200 保持不变。证据 artifacts/v0518-recovery/crash-recovery-smoke.txt 与同目录 server-failure 日志。
- 官方地图的首次启动也等待实际地图和 Steam 身份核验。恢复后的官方图选择在下次打开启动器时可恢复。
- 核心测试全部 103 项通过。未验证好友公网加入；重启后匿名身份需重新复制。

## 2026-10-13 v0.5.19 社区地图实际修复

- 纠正 v0.5.18 的原因判断：Cache 工坊 VPK 校验成功；失败的 terrorist_team_intro/end_of_match 名称命中原游戏 bin/win64/vpk.signatures 的官方 MD5 项。原路径失败，同文件通过隔离运行目录（不带 vpk.signatures）成功加载；未改地图包。
- LanternDedicated/game/bin/win64 复用游戏二进制硬链接，game 各资源目录及服务器 Steam 工坊缓存使用目录联接。只有服务器进程从隔离目录运行，且要求启用 insecure 终端模式。客户端原目录 vpk.signatures 不删除、不更改。
- 每次启动同步长度/更新时间变化的二进制硬链接；拒绝意外重定向的运行目录、错误资源链接和隔离目录中的 vpk.signatures。识别与重启同时支持旧服务器和隔离服务器路径；旧路径服务器切工坊图前要求重启迁移。
- WPF 实机验收通过：Cache 初次开服 -> Redline -> Cache 身份不变 -> 再重启成功进入 Cache。客户端 PID 33200 未改变，原 vpk.signatures 逐字节一致。
- 证据：artifacts/v0519-community/community-compat-smoke.txt；全部 103 项核心测试通过。此次未验证异地好友网络。
- 补充实测：本机客户端通过 Steam 服务器身份连接 Cache，服务器 plain status 确认 active humans=1，status_json 确认 de_minecraft_cache_new / 3783280189 / UDP 27016。

## 2026-10-13 v0.5.20 指定队伍批量加人

- T +1 / CT +1 替换为独立的 T / CT 自定义数量输入和添加按钮，默认 1，范围 1–32，位于人机管理最上方。
- 旧实现只增加 bot_quota，指定队伍满员时配额控制器可能转加另一队。新实现逐个 bot_add_t/ct，读取实际入队事件，成功后立即纠正 bot_add 可能双增的配额；满员不提高配额，不继续发命令。核对新增机器人身份及原有人机保留，未确认不报告成功。
- status 高频请求会限流，仅读操作有限重试。状态 userid 含代次，匹配入队日志的 slot 时使用低 8 位。空房休眠时提示先加入，避免休眠人机自动清除导致误报。
- 实测 Minecraft Dust II：CT 可达到 15；满 CT 请求 +3 返回 0/3，人数保持不变；T +3 返回 3/3，总人机 16 -> 19，入队日志均 TERRORIST。未绕过地图或模式容量限制，不改随机出生设置。
- 109 项测试通过，包含模拟 9 CT 加 3，只成功 1 后满员停止、禁止转加 T / 配额增长、恢复日志状态、重复槽位代次等。界面渲染图 artifacts/v0520-team-bots/terminal-category-0.png。
- 已恢复实测期间临时启用的服务器日志和空服保持唤醒设置；客户端与服务器未重启。

## 2026-10-13 v0.5.21 批量添加中断修复

- 复现 v0.5.20：bot_add 与随后配额纠正分成两个网络命令，引擎在间隔内生成第二个人机，再随机踢掉一名；原核验只追踪第一名，导致只加一人就停止。
- 加人和配额纠正在同一命令缓冲区执行；持续记录实际入队事件并验证最终留下的人机、原有人员和净增加数量。每完成一人实时更新 X/N。满队回撤配额并核对玩家列表，不静默声称成功。
- 真实独立服务器 PID 5008 / de_minecraft_dust2：T 连续新增 9 后队满，6 -> 15；CT 完整新增 10，15 -> 25，均有队伍事件和连续人数核验。证据 artifacts/relay-trial/20261013-141909-2122.log 与 20261013-142014-2122.log。
- 清理测试新增 userid 时服务器已无人休眠；随后 T +10 明确返回先加入游戏提示。未强制重新加入或重启游戏。完整 T +10 本轮未实测，CT +10 已实测。
- 全部 113 项检查通过，新增完整十人模拟、同缓冲配额纠正、存活人机队伍核验和满队回撤检查。

## 2026-10-13 v0.5.22 Prophunt 入口识别

- 用户日志与实时 status_json 均为 map=de_dust2 / addon=3758986810。plain status 同时存在 de_dust2 与 prophunt_dust2 spawn group。
- 只读解析真实工坊包 addoninfo.txt：maps=["de_dust2"]；cfg/maps/de_dust2.cfg 加载 spawn_group_load prophunt_dust2。因此包内 prophunt_dust2.vpk 是叠加场景，不是实际入口。旧扫描器把所有 maps/*.vpk 当入口，核验错误。
- 扫描优先使用 addoninfo 声明入口，限制元数据读取 64 KiB、严格地图名称字符；未声明时保留原有索引扫描。基础官方地图仍作为这个工坊包的入口，核验同时要求对应 addon，不会误认纯官方 Dust II。
- 116 项检查通过，包含实际基图入口声明、名字校验、内嵌 VPK addoninfo 数据解析；真实包返回 de_dust2。
- 独立服务器旧 PID 29292 在只读诊断 status/maps/help 后退出，目录存在 18:55:43 accessviolation dump。本轮未解析 dump 调用栈，不能认定和入口误识别是同一故障。
- 恢复独立服务器 PID 29316，客户端 PID 8648 未改变。host_workshop_map 3758986810 后确认 map=de_dust2 / addon=3758986810 / UDP27016；plain status 确认 prophunt_dust2 场景加载，game_type=3/game_mode=0。
- 服务器加载日志仍有自定义 cfg invalid commands 和可见性/光照资源警告；本轮确认服务器加载与房间身份，没有强制客户端加入，未验收实际玩家游戏流程。
- 证据 artifacts/relay-trial/20261013-190304-2122.log、20261013-190328-2122.log；新版真实房间界面检查 artifacts/v0522-addon-entry/room-smoke.txt。

---

# 2026-10-21 v0.5.23 终端传输迁移（-netconport → -concommandpipe）

CS2 1.41.7.8 移除了 `-netconport`，原有的终端、换图、人数与人机管理全部失效。本节记录重建后的验收。

- 传输层：命令走命名管道，回显读子进程 stdout。**协议原语零改动**——`status_json` 解析、echo 标记配对、`ActiveHumans`、`BotIds`、`TeamBots.Joined` 均沿用。
- 两条硬约束已实测并写进代码注释：两个管道必须在 cs2 启动前存在（否则引擎在 `inputservice.cpp (408) CConsoleCommandPipes::Create` 断言并写 minidump）；路径必须是 `\\.\pipe\NAME` 全形（两条反斜杠），`.\pipe\NAME` 与裸名同样断言。
- out 管道实测恒 0 字节（v2–v12），引擎只往 stdout 写，因此 `ConsolePipe` 不以 out 管道连上作为就绪条件。
- 121 项单元测试通过，其中两条加人场景已从旧的 TCP 模拟改为**真实命名管道 + 假引擎喂 stdout**，覆盖管道读写与 stdout 泵两条真实路径。
- 真实 CS2 1.41.9.1 端到端：管道被接受、`status_json` 解析出 `map=de_dust2`、echo 标记回到 stdout、`quit` 退出码 0。
- 加人协议真实复验：发 `log on` 后收到 2 个 CT 入队事件（`Slingshot<2><BOT>`、`Arno<3><BOT>`），`clients_bot` 到 4。
- 终端各面板发出的每一条命令都发给了真实独立服务器：**没有一条是引擎不认识的**。唯二两条 `Unknown command` 来自引擎自带的 `gamemode_competitive.cfg`（`mp_weapons_glow_on_ground`、`sv_gameinstructor_enable`），与本启动器无关。
- 启动器自检：`--smoke --restart-smoke --terminal-smoke` 全部通过。独立服务器 PID 57828 → 55760，工坊图 `ttt_cgmentos_minecraft` 换图并核验实际地图与身份 `[A:1:3674538005:51999]`；客户端 PID 保持。
- 本机边玩边开（Listen）模式**不再提供终端**：Steam `-applaunch` 起的客户端不提供 stdout，管道能连但读不到回显，任何"执行并核验"都会假成功。该模式的相关入口改为明确说明；换图与人数管理请用独立服务器。
- 关闭启动器现在会先正常停服（发 `quit`，超时才只结束该进程），并新增托盘图标与「最小化到托盘 / 直接退出」可选。此前关窗会退到后台却无托盘图标。
- 未验收：好友端跨网络实际可达性与对局内实际体验；本次只验证传输层与管理命令。

---

# 2026-10-21 v0.5.29 网页控制台

目标：让别人用浏览器看房间、换地图、管人机、执行命令，不用装启动器。房主自己用 frp 转发端口，启动器只报内网地址。

## 为什么不用 HttpListener

`HttpListener` 绑定非 localhost 地址需要 URL ACL 保留，等于首次运行要管理员权限。改用裸 `TcpListener`，普通用户就能监听局域网端口。代价是 HTTP 的请求行、查询串、响应头都要自己写，所以这些解析单独测过（见下）。

## 功能边界（刻意不做的事）

- **不能启动、停止、重启服务器**。停服会结束所有人的对局，这个决定只属于坐在那台电脑前的人。`/api/stop`、`/api/start`、`/api/restart`、`/api/shutdown` 都不存在，冒烟测试逐个 POST 断言它们返回非成功。
- 换图、加/清人机、补位、重开、执行任意控制台命令：这些房主在终端窗口本来就能做，网页只是换个入口。

## 鉴权

- 口令 10 位十六进制，由 `RandomNumberGenerator` 生成，保存在 `preferences.json`（`WebToken`），重启启动器不变。
- 比对用 `CryptographicOperations.FixedTimeEquals`，长度不等直接拒绝。
- 登录成功种 `HttpOnly; SameSite=Strict` 的 cookie；`/api/login` 是唯一免 cookie 的接口。
- 启动器的「打开网页」把口令放在 URL **片段**里（`#token=…`）。片段不会发给服务器，不会进请求日志或代理，页面读到后立刻 `history.replaceState` 清掉，再走一次 `/api/login`。

## 已实测（`--smoke --web-smoke`，真服务器 + 真 socket + 真浏览器形状的请求）

```
PASS page served at http://127.0.0.1:57338/ (15636 bytes, no external requests)
PASS wrong token rejected; unauthenticated state read refused with auth:false
PASS login set an HttpOnly session cookie
PASS state read: running=True, map='seek_summervillage_rp', humans=0, maps=61, capacity=16
PASS unknown map refused with an explanation: 没有这张地图。
PASS web map switch seek_summervillage_rp -> de_inferno confirmed by the server's own status
PASS command reached the engine and its reply came back: webecho5cd73a57
PASS no server start/stop/restart route exists
PASS request line parsing rejects junk and splits query strings
PASS 15 requests served over a real TCP socket on port 57338
```

两条断言值得单独说明，因为它们各自堵住了一类假成功：

- **换图不认接口自己的回答**。`/api/map` 返回 `ok:true` 之后，测试继续轮询 `/api/state`，直到服务器 `status` 报出的地图名真的变成目标地图才算通过。接口说成功而引擎没换图，会被判失败。
- **命令回执必须来自引擎**。启动器发命令时会先记一行 `> echo …`，所以「日志里出现了标记」并不能证明命令到了服务器。断言要求那一行**不以 `> ` 开头**，即真的是引擎回来的输出。

## 离线测试（147 项，新增 15 项）

- 请求行：正常解析、截断拒绝、相对目标拒绝、非字母方法拒绝。
- 查询串：解码 `%E4%B8%AD%E6%96%87`、无值键保留、空串与 `&&` 返回零项。
- 端口：`80`（特权）与 `70000`（越界）构造即拒。
- 真 socket 往返：页面 200 + `text/html` + `Cache-Control: no-store`；查询参数到达处理函数；按方法+路径计数正确。
- 端口被占用时 `Start()` 抛 `InvalidOperationException` 且消息含「无法监听」，不是裸露的 `SocketException 10048`。

## 界面

- 启动器顶栏新增「网页控制台」按钮（与「好友联机」「游戏终端」并列），点了会切到「服务器」页并把该区滚进视野。
- 该区：启用开关、端口（1024–65535，失焦生效）、内网地址下拉、打开网页 / 复制地址和口令、口令明文显示、状态行。
- 网页本身零外部请求：没有 CDN、没有字体下载、没有构建步骤，一个 HTML 字符串。好友在转发端口后面打开时，每个额外请求都是多一个失败点。
- 截图：`%LOCALAPPDATA%\CS2Lantern\web-console3.png`（网页，1100×1000）、`web-layout.png`（启动器该区，1320×940）。

## 未验收

- 公网转发后的真实访问（房主自己配 frp，本轮只在内网地址上验证）。
- 多人同时打开页面时的实际观感；状态读取做了 2 秒复用，避免每个观看者都去问一次服务器，但没有做过并发压力测试。
- 手机浏览器的实际排版（响应式栅格是写了，没有真机截图）。
