后续局内持续同步修复与 1.4.2.4 联测说明：见同目录 InRaidAudit.txt。当前套件共 19 组检查。

多人进图同步优化验证（2026-10-25）

分支：codex/network-sync-optimization
原始基线：1da0e817f83d7e66d8b870f34bf598036e8625d4
当前基线：codex/lagging-fixes @ 4508609；新增提交审查及适配说明见 LaggingFixesAudit.txt。
范围：直连收包、进图环境/掉落物/敌人快照和敌人实例化。未修改基地仓库实现。

定位到的放大点
1. LiteNetLib.PollEvents 在一帧内调用所有积压的游戏 RPC，叠加 Unity 创建物品/角色开销。
2. 多个进图回调重复请求环境和掉落物；每个玩家请求都重新扫描场景并一次发出所有掉落物分块。
3. AI 请求在可靠发送队列 >=64 时直接返回，客户端只能等超时；构建快照后只留下 id，发包时又构建一次。
4. 客户端的“每帧创建一个敌人”同时也是“只允许一个异步创建任务”，等待资源加载时后续敌人一直排队。
5. 提供的旧日志存在大量 HostUnreachable；重复错误及堆栈会进一步占用主线程。该错误本身仍表示链路不可达。

实现
- 直连 RPC 分发使用 4ms / 512 条的每帧预算；原生事件入队仍由 LiteNetLib 执行。
- 仅合并同一连接、实体、消息类型和通道的可替换位置/动画/AI 状态，可靠消息保持顺序。
  位置和 AI 状态按协议中的时间戳保留更新的数据，避免 UDP 乱序覆盖新状态。
- 接收缓冲区由传输层持有到分发完成；替换、断线、停网和处理异常均释放一次。
- AI、环境和掉落物分别使用 2ms 的共享调度预算，每轮每个玩家最多一个分块；拥堵玩家保留任务并让出轮次。
  这些是检查点预算，单次 Unity 调用/单个 RPC 本身仍可能超过预算。
- 掉落物按客户端就绪请求启动；移除主机初始化时向尚未就绪客户端重复广播。
  场景注册表只在场景初始化/首次需要时扫描，发送分块时读取当前权威物品状态。
- 分块间保留拾取删除标记，禁止未实例化的旧快照物品复活；新 reset 在实时生成之前应用。
- AI 请求只排 id，发送时构建内容；拥堵时保留请求，空场景也回包。先刷新本帧伤害，再发 AI 快照。
- 敌人每帧仍最多启动一次创建，允许两个异步创建任务在等待资源时交错执行。
- 在切图、重连或客户端重新就绪后清理/重启快照；保留可靠通道顺序；rebase 后追加的健康时间戳兼容说明见 LaggingFixesAudit.txt。
- 收包仍有积压时最多每 5 秒输出一条 [Direct] Receive backlog 诊断；网络错误同样限频并汇总。

自动验证（从仓库根目录）
  dotnet run --project Verification/NetworkScheduling/NetworkScheduling.csproj -c Release

测试直接编译生产调度器和 Direct 传输，使用仓库的 LiteNetLib.dll。传输测试替换 Unity 日志接口；AI 快照协议测试另使用 Unity 值类型替身，且不构建嵌套物品。
覆盖：可靠事件顺序、UDP 乱序、同连接多实体隔离、缓冲区生命周期、拥堵轮转、reset/断线、
快照空分块、快照期间拾取、非 reset 的后续快照和日志限频。
实测使用一个本机 UDP 接收端和七个真实 UDP 客户端，并通过注入时钟模拟每次 RPC 的处理耗时。
结果：280/280 条可靠消息按序交付；1414 条位置采样合并为 14 次回调，14 个连接/实体组合均保留最新状态。
纯队列压力用例：21000 条状态采样保留 21 条最新状态，70 条可靠交互全部保留。
这些结果验证调度行为，不代表真实游戏延迟百分比或帧率收益。

构建验证
使用游戏目录中的程序集：D:\SteamLibrary\steamapps\common\Escape from Duckov\Duckov_Data\Managed
本机 .NET SDK 8.0.100 的默认编译器不能编译仓库原有 Polyfill 8.9.0 的 ref struct 接口。
验证使用已安装的 Visual Studio 2022 MSBuild，加临时 NuGet Microsoft.Net.Compilers.Toolset 4.14.0（net472）。
构建参数中的 DUCKOV_MODS_DIRECTORY 必须指向本地 artifacts，避免默认 CopyToModsFolder 目标覆盖游戏 DLL。

PowerShell 构建命令（该机器上的临时编译器已还原）：
  & 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' EscapeFromDuckovCoopMod.sln /t:Build /p:Configuration=Release '/p:DUCKOV_GAME_DIRECTORY=D:\SteamLibrary\steamapps\common\Escape from Duckov' "/p:DUCKOV_MODS_DIRECTORY=$PWD\artifacts\mods" '/p:RoslynTargetsPath=C:\Users\Administrator\.nuget\packages\microsoft.net.compilers.toolset\4.14.0\tasks\net472' /p:UseSharedCompilation=false /nologo /v:minimal

产物：artifacts\mods\联机Mod1\EscapeFromDuckovCoopMod.dll
游戏目录、用户配置以及主工作目录的源代码均未由本次优化覆盖。

仍需游戏内复测
- 2/4/8 人直连进入同一地图，记录最后一名玩家的物资就绪、敌人出现、命中到扣血的延迟和主机帧时间。
- 让一名慢客户端晚进入/重连，并在其快照期间由其他玩家拾取或丢弃物品、击杀敌人和开关门。
- 在快照尚未结束时切图、断线和重连，确认无旧场景物品、重复敌人或血量回退。
- 合并另一 session 的仓库修复后再进行多人仓库联测，避免直接用此 DLL 覆盖另一分支的修复。
- 发送端持续位置广播的连接数增长、底层带宽/丢包及单次游戏对象创建的耗时仍需要实机分析。

Rebase 后适配验证：15 项自动检查通过，包括健康采集顺序、同帧变化、击杀事件保留、死亡许可及 AI 快照新旧尾部边界。
