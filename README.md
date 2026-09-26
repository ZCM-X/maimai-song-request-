# SongRequest · 点歌台 mod

游戏里跑一个本机网页：搜歌、点难度，游戏直接跳过去。**不改任何游戏文件。**

## 致谢

曲目别名数据来自 **MuNET 别名数据库**，原始社区别名由 **[clansty](https://github.com/clansty) 的 SongSearch 项目**整理与贡献 —— 感谢作者与社区贡献者。

稳定性修复（v1.0.5 的 SSE 主线程写网络、曲目表并发、点歌状态机等）来自 **[chidi0716](https://github.com/chidi0716)** 的报告与实现 —— 感谢。

## 功能

- **曲目表实时来自游戏**：换版本、热导入自制谱都会自动跟上，不用改配置
- **网页搜索**：曲名 / 艺术家 / 曲目 ID / 社区别名（空格分词、忽略大小写与全半角）
- **点难度块直接跳曲**：BASIC…Re:MASTER 五档带等级（`13+` 原样），点了游戏就跳到该曲该难度
- **DX / 标准** 徽章与筛选；该版本玩不了的谱面按游戏判断置灰
- **曲绘封面**：直接从游戏资源取图
- **实时游玩面板**：曲名 / 难度 / Combo / 分数 / 判定 / Life（SSE 推送）
- **手机可用**：同局域网手机浏览器打开即点歌；另有 OBS 透明浮层 `/overlay`

## 安装

**将 dll 放置在游戏的 mods 文件夹，游戏加载完成后浏览器打开本地 IP 加 8790 端口即可点歌，例 `127.0.0.1:8790`。**

- 需要 [MelonLoader](https://melonwiki.xyz/) 0.6.x（Mono 版本）
- 构建：`dotnet build src/SongRequestMod.csproj -c Release -p:GameDir="<游戏Package目录>"`

更多：接口 [`docs/API.md`](docs/API.md) · 开发笔记 [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) · 使用说明 [`docs/使用说明.md`](docs/使用说明.md) · 数据来源 [NOTICE](NOTICE) · 许可 [MIT](LICENSE)
