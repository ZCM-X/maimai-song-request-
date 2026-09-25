# 本机接口 (API)

默认端口 `8790`，只监听本机 +（配置开启时的）局域网 IP；本机 / 局域网**无鉴权**。

**远程分享**（经 cloudflared 隧道进来、带 `Cf-Ray` / `Cf-Connecting-Ip` / `X-Forwarded-For` 头的请求）必须带本次分享的密钥：
首次用 `?k=<密钥>` 打开，服务端写 `srk` cookie，之后同一浏览器的请求自动带上；密钥不对回 403。
远程访问者不能用 `/api/selfcheck`、`/api/selftest`、`/api/remote*`。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/` | 点歌台页面（内嵌；`Mods\SongRequestMod\page.html` 存在时优先磁盘文件，方便改 UI） |
| GET | `/overlay` | OBS 透明浮层 |
| GET | `/api/songs[?refresh=1]` | 全部曲目 JSON |
| GET | `/api/nowplaying` | 当前游玩状态 |
| GET | `/api/status` | 轻量状态（`rev` 变化=曲库变化，网页据此自动刷新） |
| GET | `/api/selfcheck` | 自检：补丁/进程/子序列/分类/光标/布防 |
| GET | `/api/selftest?id=&diff=` | 布防：下次进选曲界面自动点这首歌这个难度 |
| POST | `/api/play` | 表单 `id=` `diff=`（省略=最高可用难度） |
| POST | `/api/random` | 表单 `diff=`（可选） |
| GET | `/jacket?id=&s=1` | 曲绘 PNG（`s=1` 小图） |
| GET | `/api/npstream` | **SSE**：`data: {nowplaying json}`，仅在内容变化时推送 |
| GET | `/api/remote` | 远程分享状态 `{"state":"off\|downloading\|starting\|running\|error","url":"带密钥的分享链接","msg":""}`（仅本机/局域网） |
| POST | `/api/remote/start` | 开启远程分享（后台启动，轮询 `/api/remote` 看进度；仅本机/局域网） |
| POST | `/api/remote/stop` | 关闭远程分享，旧链接立即失效（仅本机/局域网） |

`/api/status` 额外返回 `viewer`：`local`（本机/局域网）或 `remote`（经分享链接）。

## /api/songs 字段

```json
{"id":15001,"name":"甘噛みでおねがい","artist":"ピノキオピー、初音ミク","genre":"POPS & ANIME",
 "bpm":138,"version":"1.70","type":"DX","std":false,"dx":true,"alias":["咬","甘噛"],"maxLevel":13,
 "difficulty":[{"type":3,"name":"MASTER","level":13,"levelStr":"13","enable":true,"playable":true}]}
```
- `difficulty[].type`: 0=BASIC 1=ADVANCED 2=EXPERT 3=MASTER 4=Re:MASTER
- `enable`: XML 声明存在 **且** 游戏认为可玩（`isExistsScore`）；false 时网页置灰
- `playable`: 仅游戏判断的原始值

## /api/nowplaying

```json
{"state":"playing","id":15002,"name":"混沌ブギ","artist":"...","difficulty":"MASTER","diffType":3,
 "type":"DX","levelStr":"13","combo":156,"maxCombo":200,"score":99.2355,"life":100,"startLife":100,
 "notes":897,"trackNo":1,"player":"PLAYER",
 "judge":{"criticalPerfect":900,"trueCritical":0,"perfect":30,"great":5,"good":1,"miss":2}}
```
- `state`: `idle` | `select`(选曲界面光标曲目，无成绩字段) | `playing`
- 选曲→游玩之间的过场（TRACK 起幕/曲间加载/结算）返回 `playing` + 曲目信息（成绩为 0），面板不会空白
