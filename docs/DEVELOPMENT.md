# 开发笔记（踩过的坑）

每条都是实际调出来的结论，能省你好几天。

**环境**：街机音游客户端 1.70 / **Mono**（非 Il2Cpp）/ Unity 2018.4 / MelonLoader 0.6.4 / Harmony 2。
**构建**：`dotnet build src/SongRequestMod.csproj -c Release -p:GameDir="<游戏Package目录>"`（net48 + ReferenceAssemblies，引用 MelonLoader\net35 与 游戏_Data\Managed）。

1. **曲目表会被别的 mod 换掉**：`DataManager.GetMusics()` 可能被别的 mod 的 Harmony postfix 过滤成几首（云海/Collection）。
   → 反射读私有字段 **`_musics`**。注意 `AssetManager.GetJacketTexture2D(int)` 内部也走 `GetMusic()`，会被同样影响，改用字符串重载。
2. **难度按 `notesData` 位置算**：这个数据包里所有 `notesType` 都是 0，不能用它当索引 → 用下标（0=BASIC…4=Re:MASTER），
   下标对应谱面文件后缀（`015001_03.ma2`=MASTER）。等级串取 `MusicLevel` 表的 `levelNum`（`13+` 原样）。
3. **"能不能选这档"以游戏为准**：游戏用 `musicSelectData.isExistsScore[i]`（不是 XML 的 `isEnable`），只看 XML 会出现"点 14+ 跳 14"。
   另：`DifficultySelectSequence` 里 `!IsRemasterEnable() && CurrentDifficulty==ReMaster` 会 `CurrentDifficulty--`。
4. **`Process.SubSequence` 既是枚举名又是命名空间名** → 静态引用必撞编译器；子序列数组一律**反射**读写。
5. **HTTP 线程不能碰 Unity** → 网页线程只入队，游戏动作全部回主线程 `OnUpdate`；封面转 PNG 也要主线程（`RenderTexture` + `ReadPixels`）。
6. **跳曲顺序**：写 `CurrentCategorySelect/CurrentMusicSelect/ScoreType` → `ChangeBGM()` → **`CalcMonitorDifficulty(true)`（必须在设难度之前，它会覆写难度索引）**
   → 写 `CurrentDifficulty[p]` / `DifficultySelectIndex[p]` / `GameManager.SelectDifficultyID[0]` → `SetScrollMusicCard/SetScrollGenreCard/OutGenreTab`
   → 下一帧切子序列到 `Difficulty(=3)` 并 `OnStartSequence()`。`MusicSelectProcess` 实例用 postfix 挂在 `OnStart` 拿、`OnRelease` 清。
7. **封面会闪**：面板每 500ms 刷新若整块 `innerHTML` 重建，`<img>` 每次被销毁重建。
   → 同曲/同状态走**原地更新**（只改文本节点），只在换歌/换难度/状态切换时重建 + 封面 URL 带 cache-buster。
8. **前端性能**：别在几千个元素上挂 `will-change`（几千个合成层）；别留无限循环 CSS 动画；整表渲染合并到 `requestAnimationFrame`；单次渲染 ~160 行上限。
   实测：`will-change` 2004→1、常驻动画 352→13、连点 8 次 270ms→4ms、单次渲染 30-48ms→11-16ms。
9. **Combo 要和游戏一致**：游戏是**离散跳变**（每判定 +1）+ 数字**缩放弹出**，无滚动插值 → 直接赋值 + 130ms `scale(1→1.10→1)`；
   并且用 **SSE** 推送而不是轮询（轮询 180ms + 快照 50ms ≈ 最坏 230ms 的延迟）。
10. **局域网**：`HttpListener` 绑具体网卡 IP 不需要管理员（`+`/`*` 才要 URL ACL）；枚举 IP 要过滤 VMware/VirtualBox/Hyper-V/ZeroTier/Radmin/TAP/WSL/Docker，但没有物理网卡时要能退回；Windows 防火墙默认挡入站。

**代码结构**：`Mod.cs`（入口/配置/Tick）· `SelectDriver.cs`（跳曲+自检）· `SongTable.cs`（曲库 JSON）· `LiveState.cs`（状态快照）· `Jackets.cs`（贴图→PNG）· `Web.cs`（HTTP+SSE+内嵌资源）· `Aliases.cs`（别名库）· `page.html`（单文件前端）
