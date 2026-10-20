# 方案调查与实现决策

调查日期：2026-10-12。以下区分官方支持、项目作者实现、社区经验和本机验证。

## v0.3 外部终端增补

调查对照：[CS2 demo input viewer 的 NetCon 实践](https://github.com/liker0704/cs2-demo-input-viewer)、[CS2RemoteConsole](https://github.com/theokyr/CS2RemoteConsole)、[vconsole-python 的协议说明](https://github.com/oxijoined/vconsole-python)。没有直接引入上述项目代码或运行依赖。

本机当前 Windows 构建中，仅 `-vconsole` 不足以开放接口，带 `-insecure` 的私服进程可以使用 NetCon。VConsole 批量配置包与短包参考格式不同，最终选择验证过的 UTF-8 文本连接，并用 `-netconpassword` 认证；只连接 127.0.0.1，不添加防火墙规则。协议无逐命令成功响应，连接用随机 echo 验证，操作结果显示引擎原始输出。详见 v0.3 验收记录，后文为最初 v0.1 调查背景，其中“不关闭 VAC”的原始决定已被用户新增私服外部终端需求与实测结果取代。

## 结论

用户选择“本机边玩边开，方便好友加入”，因此优先复用 CS2 内置本地服务器和 Steam 好友系统。Windows 单平台采用 WPF，地图扫描／设置／启动由同一原生桌面进程处理，启动后可退出。独立服务器 IP 直连保留作备选；独立 P2P 属于额外实验能力。

这减少启动器常驻服务和重复运行时，也避免为了临时好友局始终同时运行客户端与独立服务器。服务器与客户端实际 CPU、GPU 和网络表现仍取决于地图脚本、人数、人机导航和房主硬件；没有将“轻量界面”冒充游戏帧率提升。

## 现有方案

| 方案 | 可借鉴之处 | 本任务选择 |
|---|---|---|
| CS2 官方 Open Party | 在工坊地图页开启，好友列表可加入本地游戏 | 首选，必须保留官方 UI 路径 |
| mortenlein/CS2-server-launcher | 安装识别、地图和模式选择；当前 README 已转为 Electron / React | 借鉴流程，自行实现原生桌面界面 |
| este-microondas/CSL | 较早的 CS2 dedicated launcher 和操作说明 | 作为已有启动器项目对照，不假设旧命令仍适用 |
| 5Stack | 独立服务器管理经验和 Steam Relay 文档 | 参考 P2P 所需网络设置，不引入整套面板基础设施 |
| SteamCMD 独立服务器 | 适合无人值守与单独主机 | 不是本机好友局的默认安装方案，不额外下载另一份游戏 |

原始来源：[Valve 官方 2026-03-18 更新](https://www.counter-strike.net/newsentry/532126482488623353)、[mortenlein 项目](https://github.com/mortenlein/CS2-server-launcher)、[CSL 项目](https://github.com/este-microondas/CSL)、[5Stack SDR 文档](https://docs.5stack.gg/servers/steam-relay)。没有复制上述项目代码。

## 原生好友加入与 P2P 不应混为一谈

Valve 在上述更新明确介绍：本地开始工坊地图前开启 Open Party，好友通过好友列表加入。因此用户其实可以不使用启动器完成基础好友开图；启动器的价值在于更好的本地地图检索和规则管理。

官方说明没有保证任意外部 `+map_workshop` 启动就等价于从游戏 UI 建立公开队伍。实现提供 `lobby_default_privacy_bits2 1` 的默认队伍设置和快捷开图，同时保留显眼的 UI 引导，不声称已经验证外部启动到好友加入的完整链路。

独立 P2P 参考 5Stack 的两个配置项：ConVars 内 `net_p2p_listen_dedicated=1`，NetworkSystem 内 `CreateListenSocketP2P=2`。其文档特别要求保留 branchspecific 原始内容，避免丢失 AppID 730。本实现保留文件与注释、原始字节备份、哈希校验恢复。Steam 更新可能覆盖修改。

匿名服务器身份与重启后变化来自第一手社区搭建讨论，属于经验而非 Valve 保证：[社区实际排障讨论](https://www.reddit.com/r/GlobalOffensive/comments/1tcomb1/first_time_hosting_a_dedicated_server_since_cs2/)、[AlliedModders SDR 讨论](https://forums.alliedmods.net/showthread.php?t=349886)。后者旧回复与更新后的文档有差异，因此文件位置以 5Stack 当前文档和本机现有 branchspecific 文件为准。P2P 没有实测前始终标记实验。

## 地图识别与下载

本机验证了 Steam 注册表路径、`libraryfolders.vdf` 的多库布局、AppID 730 的 `appmanifest_730.acf`，以及 `steamapps/workshop/content/730/<id>`。扫描读取 `publish_data.txt` 的标题；VPK 只读目录索引。

实际工坊文件不是一定直接包含 `maps/*.vmap_c`：本机 Cobblestone 的外层 VPK 包含 `maps/de_cbble_csgo.vpk`。因此解析器同时识别这两种索引形式，跳过非地图内容与分卷数据文件，校验内嵌数据范围，拒绝不完整文件。没有解压整图，也没有按工坊 ID 猜地图名称。扫描范围限制在 Steam 库和用户指定目录，并跳过 reparse points。

在线信息使用 Valve 的 [GetPublishedFileDetails](https://partner.steamgames.com/doc/webapi/ISteamRemoteStorage)。它可获得地图公开信息；订阅相关接口涉及发行商权限，不能把它当作普通第三方启动器的免登录订阅接口。实现采用 Steam 官方页面由用户订阅、Steam 下载、本地重新扫描的闭环。集合、非 CS2 或缺少 Map 标签的远程条目不会直接当地图启动。

## 启动与配置

实际客户端二进制给出的语法是 `map_workshop <addonname> <map_name> [option1=value1 ...]`。已通过本地只读字符串检查确认。Listen 模式传入工坊 ID 与索引中的真实地图名；dedicated 使用 `host_workshop_map <id>`。命令存在性对照来自 [ArminC 的引擎命令导出](https://github.com/ArmynC/ArminC-CS2-Cvars/blob/main/cvars/cvarlist.md)。

模式配置先于开图；通过 `lservercfgfile` / `servercfgfile` 指向独立规则文件。地图脚本与游戏模式有自己的生命周期，提供载入后重新执行规则的按钮，避免承诺所有地图始终服从通用 cvar。

不加入旧 CS:GO 的 128 tick 优化、不设置实时进程优先级、不修改线程亲和性或关闭 VAC。它们不是完成地图选择与好友加入的必要条件。

## 技术选择

[WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) 支持 Windows 原生桌面控件与虚拟化列表。代码使用 .NET 标准库，无第三方运行依赖；便携版包含 .NET 运行时，文件较框架依赖版大，这与运行时资源消耗应分开比较。客户端只在扫描／网络请求时进行相应工作，地图列表虚拟化，封面受并发与体积限制，启动后没有后台轮询游戏状态。

后续优先级：两台实际 Steam 客户端验证 Open Party 快捷开图 → 规则载入时机 → 独立 IP → P2P 与不同 NAT 环境。只有获得真实连接记录后，才能把这些链路从“已生成和执行启动流程”提升为“已端到端验证”。


## 2026-10-12 v0.4 补充

核对 [CS2 引擎命令导出](https://github.com/SteamTracking/GameTracking-CS2/blob/master/DumpSource2/commands.txt) 的 bot_add_t/ct、mp_warmup_start/end、mp_pause_match/unpause_match、mp_swapteams；本机 help 确认暂停在下一冻结期，交换阵营会重开。未使用第三方插件或内存读取。

实测 status_json 返回 server.map / clients_human / clients_bot 和客户端身份，但没有队伍字段。精准补位使用用户填写的两队真人人数，执行前校验真人总数；不根据总人数猜测 2v5 与 3v4。旧 bot 先清零后通过 bot_add_t/ct 按队补齐；保持真人原队伍。没有把 competitive quota 当作均衡补位模式。

工坊入口使用 steam://openurl/https://steamcommunity.com/app/730/workshop/，条目使用 steam://url/CommunityFilePage/ID。删除只处理本地工坊包并使用 Windows 回收站，不修改 Steam appworkshop 清单或伪造取消订阅。


## v0.5 本机诊断与加入方案

- 用户提供的 3623775808 (deathrun_minecraft_snow，约 10.25 MiB)、3636075272 (Train Minecraft，约 516.07 MiB) 均由 Steam API 验证为 CS2 地图；当时本机订阅缓存/下载日志/完整文件没有这两个 ID。另一个条目 3408790618 的日志明确出现 Not enough free disk space，不混同为这两张图的错误证据。
- E 盘当时剩余约 150–180 MiB；CS2 自身清单另有约 1.37 GiB 下载、2.00 GiB staging。只读磁盘候选记录见 artifacts/e-drive-readonly.json 和 E盘只读检查.txt。未删除文件。
- 当前实际 NetCon status_json 曾返回 fruitninja + [A:1:0:0]，之后用户换图又返回有效身份。因此身份必须每次读取并校验，不能一直复用旧码。真实接口及主窗口连接 UI 已验证，好友端实际可达性未验证。
- [Valve Open Party 说明](https://www.counter-strike.net/newsentry/532126482488623353) 只描述游戏工坊菜单入口，不能据此保证直接 map_workshop 的大厅加入可用。
- [Steam 下载暂停说明](https://help.steampowered.com/en/faqs/view/4F9E-6328-E9B8-47F9) 与 [下载管理](https://help.steampowered.com/en/faqs/view/71AB-698D-57EB-178C)；程序只显示状态并打开下载页，不修改 Steam 下载设置。
- [Radmin VPN 官方帮助](https://www.radmin-vpn.com/help/index.html)：双方创建/加入同一个网络，再使用房主 VPN IP。当前机器已有 Radmin 适配器，但这不证明好友已加入或 UDP 已可达。
