# SongRequest · 街机音游 点歌台 mod

在游戏里跑一个本机网页，把全部曲目/难度列出来 —— 搜到歌、点难度，游戏直接跳过去。
全部在内存里完成（Harmony + 反射），**不改任何游戏文件**。

> **Song Request console for 街机音游.** A MelonLoader mod that serves a local web page listing every
> song and difficulty in your game. Search, click a difficulty, and the game jumps to that chart.
> Everything is done in-memory (Harmony + reflection); no game files are modified.

---

## 致谢 / Credits

- **曲目别名数据来自 MuNET 公共 API**（https://api.mumur.net），原始社区别名由 **[clansty](https://github.com/clansty) 的 SongSearch 项目**整理与贡献 —— **感谢作者与社区贡献者**。本项目仅做字段提取与格式转换，详见 [NOTICE](NOTICE)。别名库可用 `tools/update-aliases.ps1` 自行更新。
- [MelonLoader](https://melonwiki.xyz/) —— mod 加载器
- [Harmony](https://github.com/pardeike/Harmony) —— 运行时补丁

## 功能

- **曲目表实时来自游戏**（不是写死的清单）：换版本、热导入自制谱都会自动跟上，网页自己刷新
- **网页搜索**：曲名 / 艺术家 / 曲目 ID / **社区别名**（空格分词 AND、忽略大小写与全半角）
- **难度直达**：每首歌 5 个难度块（BASIC…Re:MASTER）带等级字符串（`13+` 原样）→ **点一下，游戏跳到该曲该难度**，并自动落到难度选择画面
- **DX / 标准** 徽章与筛选；该版本没有的谱面会置灰不可点
- **封面**：直接从游戏资源里取图（游戏贴图 → PNG），不是外部图源
- **「现在在玩」面板**：曲名 / 难度 / 类型 / **Combo** / MaxCombo / 分数 / 5 项判定 / Life，SSE 实时推送
- **手机可用**：同局域网手机浏览器打开即可点歌（含响应式布局与 44px 触摸热区）
- **OBS 浮层**：`/overlay` 透明背景，只有曲名/难度/Combo/判定

## 安装

**将 dll 放置在游戏的 mods 文件夹，游戏加载完成后浏览器打开本地 IP 加 8790 端口即可点歌，例 `127.0.0.1:8790`。**

1. 装好 [MelonLoader](https://melonwiki.xyz/) 0.6.x（游戏为 Mono，不是 Il2Cpp）
2. 把 `SongRequestMod.dll` 放进 `<游戏目录>\Mods\`
3. 重启游戏，日志里会打出地址：

```
[SongRequest] v1.0.0.0  本机: http://127.0.0.1:8790/    手机(同网): http://192.168.x.x:8790/
```

4. 浏览器打开那个地址（**页面 / OBS 浮层 / 别名库全部内嵌在 dll 里，只需这一个文件**；首次启动会生成 `SongRequestMod.toml`）

## 使用

- **点歌**：搜索 → 点该曲右侧的**难度块** → 游戏跳到该曲该难度
- **必须在选曲界面**（正在打歌 / 标题画面时点会提示"当前不在选曲界面"）
- 快捷键：`/` 聚焦搜索、`Esc` 清空、`↑↓` 移动高亮
- 手机：连同一个 WiFi，浏览器打开日志里"手机(同网)"那个地址

### 手机连不上？

多数是 **Windows 防火墙挡了入站**。在**管理员** CMD 里跑一次：

```
netsh advfirewall firewall add rule name="SongRequest" dir=in action=allow protocol=TCP localport=8790
```

### 安全提示

网页服务**没有鉴权**，并且默认也会绑定局域网地址（为了手机点歌）。同网络下的任何人都能打开这个页面并切歌。如果只在单机用，把配置里 `局域网访问` 改成 `false`，就只监听 `127.0.0.1`。

## 配置 `SongRequestMod.toml`

| 项 | 默认 | 说明 |
|---|---|---|
| `启用` | true | 总开关 |
| `网页` | true | 是否启动本机网页 |
| `网页端口` | 8790 | 端口被占就改这个 |
| `局域网访问` | true | 手机同网访问；关掉只监听本机 |
| `跳转后进难度画面` | true | 关掉只移动光标、不切画面 |
| `封面服务` | true | 网页显示曲绘封面 |
| `详细日志` | false | 打开后输出过程日志（排查用） |

## 本机接口

`GET /api/songs` · `GET /api/nowplaying` · `GET /api/status` · `GET /api/selfcheck` · `GET /api/selftest` · `POST /api/play` · `POST /api/random` · `GET /jacket?id=&s=1` · `GET /api/npstream`（SSE 实时推送）
详见 [`docs/API.md`](docs/API.md)。

## 别名库

`aliases.txt` 是制表符分隔的纯文本，直接编辑即可（保存后刷新网页生效，不用重启游戏）：

```
# 曲名(或曲目 id) <Tab> 别名 <Tab> 别名 ...
独サカリ	咬	甘噛
15001	咬
Don't Fight The Music	dftm	刹那	你不许玩了
```

匹配规则很朴素：**大小写不敏感的子串匹配**（不做拼音/罗马音转换），想要什么写法就往文件里加一列。

## 构建

```bash
# 需要本机已装游戏（用于引用 Assembly-CSharp.dll / MelonLoader.dll）
dotnet build src/SongRequestMod.csproj -c Release -p:GameDir="<你的游戏 Package 目录>"
```

产物在 `bin/Release/SongRequestMod.dll`。开发笔记（踩过的坑、为什么这么做）见 [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md)。


## 许可

MIT，见 [LICENSE](LICENSE)。第三方数据说明见 [NOTICE](NOTICE)。

## 已知限制

- 只在 **街机音游客户端 1.70 / Mono + MelonLoader 0.6.4** 上验证过；其他版本可能需要在 `docs/DEVELOPMENT.md` 里提到的 API 差异处调整
- 选曲列表由游戏自己维护，刚热导入、还没进选曲列表的曲子会提示"找不到这首歌"（回一次标题再进选曲界面即可）
- 曲目别名库是社区数据，覆盖率不保证 100%
