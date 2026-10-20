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
