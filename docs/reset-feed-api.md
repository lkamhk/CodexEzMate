# Codex EzMate Reset 接收端接口文档

文档版本：1.0 · Feed 协议：`schemaVersion = 1` · 核对日期：2026-09-24

适用对象：公告收集器、Feed 发布服务的开发者，以及负责联调的 AI／工程师。
本文以当前工作区的接收端实现为准，不表示该功能已发布，也不表示已部署正式公告源。

> **接口方向：EzMate 主动 GET 远端 HTTPS JSON Feed。不是收集器 POST 到 EzMate。**
> 接收端没有开放 HTTP 监听端口、Webhook、WebSocket 订阅或通知回执接口。
> 当前只需部署一个可信、可直接读取的 JSON 地址，再由用户在 EzMate 中配置。

## 1. 接入概览

```text
公开消息 → 收集器核实、分类、跨来源去重 → HTTPS JSON Feed
                                             ↑ GET
                                    EzMate ResetWatchService
                                      ├─ 本地缓存与修订去重
                                      ├─ Reset 动态／系统通知
                                      └─ 请求既有用量流程重新读取
```

| 项目 | 当前契约 |
| --- | --- |
| URL | 用户配置的完整 HTTPS 地址；没有固定域名或固定路径。 |
| 请求 | `GET`，无请求体；接收器不自动添加业务查询参数。 |
| 鉴权 | 不提供 API Key、Bearer Token 或 Cookie 登录配置；不得要求 Codex 账户凭据。 |
| 正常响应 | `200 OK` + 一个完整的 Feed JSON 对象。 |
| 缓存响应 | 具有有效缓存及对应条件请求时返回 `304 Not Modified`。 |
| 包装格式 | 根对象直接包含 `schemaVersion` 等字段，不能包在 `data`、`result` 中。 |
| 大小 | 响应体最多 524,288 字节（512 KiB），解压后的实际读取也受限。 |
| 超时 | 单次 HTTP 请求及响应读取共用 15 秒超时。 |
| 轮询 | 默认 5 分钟，可设 5–60 分钟；另加 0–30 秒随机延迟。 |
| 首次接入 | 静默建立基准，不弹历史通知，也不因该批公告请求账户刷新。 |
| 正式地址 | 当前未预设，功能默认关闭。本文所有 `example.com` 地址均为占位示例。 |

推荐服务地址形式：`https://example.com/ezmate/reset-feed.json`。这只是示例，不是已部署接口。

## 2. HTTP 请求与响应

### 2.1 首次请求

```http
GET /ezmate/reset-feed.json HTTP/1.1
Host: example.com
Accept: application/json
```

接收端设置 `Accept: application/json`，支持 gzip／deflate 自动解压。
它不发送 Codex Token、账户 ID、Cookie、对话或本机用量；Feed 主机仍能看到 IP 等一般连接信息。
接收端没有实现签名鉴权、自定义鉴权请求头或客户端注册流程。

### 2.2 返回完整 Feed

```http
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
ETag: "reset-feed-demo-v1"
Last-Modified: Thu, 24 Sep 2026 12:00:00 GMT
Cache-Control: public, max-age=60

{ ...完整 Feed 对象，见第 9 节... }
```

上面的正文是示意，不是可直接解析的 JSON。`Content-Type` 建议按例设置；当前接收端直接解析正文，
并不以该响应头作为独立校验条件。`ETag`、`Last-Modified` 均可省略；没有它们时仍能下载和去重。
示例的 `Cache-Control` 是发布建议，不会改变 EzMate 自己的轮询间隔。

### 2.3 条件请求与 304

如果上次响应提供有效缓存标记，后续请求可包含：

```http
If-None-Match: "reset-feed-demo-v1"
If-Modified-Since: Thu, 24 Sep 2026 12:00:00 GMT
```

内容未变时，服务端可返回无正文的 `304`。EzMate 复用已有 Feed；本次本地成功检查时间会更新，
但 Feed 内的 `generatedAt` 和来源检查时间不会因此变新。无缓存却收到 `304`，会作为无效结果处理。
`If-None-Match` 只会携带可解析且长度不超过 1,024 的已有 ETag；服务端应使用短而合法的 HTTP ETag。

**心跳也属于正文变化。** 收集器更新 `generatedAt` 或任何来源状态后，应该更新缓存标记并返回新正文。
不能一直返回旧正文或 `304`，再期望接收端认为来源仍然新鲜。

### 2.4 网络限制

所有 Feed 地址及 `links[].url` 必须是绝对 HTTPS URL，最长 2,048 个 .NET 字符长度单位，
不允许 URL 用户名／密码、`#fragment` 或控制字符。查询参数可以存在，但不要在其中放入密钥；
完整 Feed URL 会保存在本地设置中。

自动重定向关闭：`301`／`302` 等不会被跟随。配置时必须使用最终 JSON 地址，不能使用登录页、
短网址或需要跳转的网页。正常服务只应使用 `200` 或有条件的 `304`；`204` 空正文不能解析为 Feed，
其他 2xx 也会按完整 JSON 读取，并不是增量响应协议。

`401`／`403`／`404`／`429`／5xx、TLS 错误及超时均按检查失败处理。当前没有专门解析
`Retry-After`，没有自动登录、分页请求、备用源切换或对远端错误正文的业务处理。

## 3. JSON 通用规则

| 规则 | 要求 |
| --- | --- |
| 编码和结构 | 使用 UTF-8、标准 JSON 根对象；不要发送 HTML、JSONP、注释或尾随逗号。 |
| 属性名 | 区分大小写，使用本文列出的 camelCase，例如 `feedId`，不是 `FeedId`。 |
| 数值 | `schemaVersion`／`revision` 为 JSON 整数，不接受 `"1"` 这类数字字符串。 |
| 枚举 | 用列出的规范字符串；不发送数字、未知值或自行创造的新状态。 |
| 必填字段 | 不能省略；非空对象、数组和字符串也不能用 `null` 代替。 |
| 可选文本 | 省略时默认 `""`；可发空字符串，但不接受显式 `null`。 |
| 可空字段 | 只有第 4 节标为可空的时间／关联 ID 可省略或使用 `null`。 |
| 重复属性 | 任意嵌套层级的同名 JSON 属性都会被拒绝。 |
| 深度 | 最大 JSON 嵌套深度为 20。 |
| 附加属性 | 当前忽略未知属性；不能借此改变现有字段语义，也不要在附加属性中放秘密。 |
| 字符串长度 | 下文字符串上限按 C# `string.Length`，即 UTF-16 代码单元计算，不等同于 UTF-8 字节数或可见字数。 |

标识符统一采用 `^[A-Za-z0-9_.:-]{1,128}$`。`feedId`、来源 `id`、事件 `id`、`relatedEventId`
均适用；大小写不同会被视为不同标识。`sourceId` 必须准确引用本 Feed 的某个来源 ID。

普通文本不执行 HTML、脚本或 Markdown 指令。文本允许换行、回车、Tab，但其他控制字符不允许；
事件 `title` 更严格，连换行、回车和 Tab 都不允许。生产者应避免在来源名和链接标签中换行。

时间使用 ISO 8601 字符串，必须有大写 `Z` 或明确 `±HH:mm` 偏移，例如
`2026-09-24T12:00:00Z` 或 `2026-09-24T20:00:00+08:00`。不接受 Unix 数字时间戳、
无时区的本地时间或随意格式化的日期。建议统一输出 UTC。接收端显示时转换为电脑本地时区。

## 4. 数据字典

### 4.1 根对象：ResetFeed

| 字段 | 类型 | 必填 | 限制与含义 |
| --- | --- | --- | --- |
| `schemaVersion` | integer | 是 | 目前只能为 `1`。不是软件版本或事件修订号。 |
| `feedId` | string | 是 | 稳定 Feed 身份，符合标识符规则。不要每次发布都换一个。 |
| `generatedAt` | timestamp | 是 | 本轮 Feed 生成时间；非默认最小日期，不能晚于客户端当前时间 5 分钟以上。 |
| `sources` | Source[] | 是 | 1–32 个非空来源对象；ID 在数组内唯一。 |
| `events` | Event[] | 是 | 0–200 个非空事件对象；ID 在数组内唯一。允许 `[]`。 |

空事件列表仍需包含 `sources` 和真实健康状态。`events: []` 只表示本次快照没有事件，
不代表收集器一定成功检查了所有来源。

### 4.2 来源对象：Source

| 字段 | 类型 | 必填 | 限制与含义 |
| --- | --- | --- | --- |
| `id` | string | 是 | 符合标识符规则，供事件链接引用。 |
| `name` | string | 是 | 最长 120；不能是空白字符串。 |
| `health` | string enum | 是 | `healthy`、`degraded` 或 `unavailable`。 |
| `lastSuccessfulCheckAt` | timestamp / null | 条件必填 | `healthy` 时必须提供非默认时间；其他状态可省略或为 null，也可保留上次成功时间。 |
| `message` | string | 否 | 最长 1,000；缺省为空。面向用户的说明，不应含令牌、内部堆栈或敏感响应。 |

`lastSuccessfulCheckAt` 不得晚于 `generatedAt + 5 分钟`，也不得晚于客户端当前时间加 5 分钟。
`healthy` 不会绕过一小时新鲜度判定。抓取失败时不能把当前时间填成成功检查时间；
应保留历史成功时间，并改成 `degraded` 或 `unavailable`。

### 4.3 事件对象：Event

| 字段 | 类型 | 必填 | 限制与含义 |
| --- | --- | --- | --- |
| `id` | string | 是 | 稳定事件 ID，符合标识符规则。跨网站转载同一件事，应复用同一个 ID。 |
| `revision` | integer | 是 | 1–2,147,483,647；递增即可，不要求连续。 |
| `title` | string | 是 | 最长 240；非空白；不能包含控制字符。 |
| `summary` | string | 否 | 最长 4,000；缺省为空，不接受 null。 |
| `status` | string enum | 是 | 见第 5 节。 |
| `evidence` | string enum | 是 | 见第 5 节，由 Feed 生产者提供分类。 |
| `impacts` | string enum[] | 是 | 1–3 个值，不得重复，见第 5 节。 |
| `publishedAt` | timestamp | 是 | 原始事件公开时间；非默认最小日期。建议同一事件始终保持不变。 |
| `updatedAt` | timestamp | 是 | 本次事件内容修订时间；不能早于 `publishedAt`。 |
| `expectedStartAt` | timestamp / null | 否 | 明确知道时才提供。可省略或为 null。 |
| `expectedEndAt` | timestamp / null | 否 | 明确知道时才提供；与开始时间都有值时，结束不能早于开始。 |
| `timeDescription` | string | 否 | 最长 1,000；保留「稍后」「未来一小时」等原文含义。 |
| `scope` | string | 否 | 最长 1,000；描述适用方案或资格，未知可省略。当前只是显示文本。 |
| `relatedEventId` | string / null | 否 | 符合标识符规则，不能引用自己。当前不要求目标出现在快照中，也不根据它合并通知。 |
| `links` | Link[] | 是 | 1–10 个非空对象；引用本 Feed 的来源。 |

`updatedAt` 不得晚于 `generatedAt + 5 分钟`，也不得晚于客户端当前时间加 5 分钟。
客户端的上述校验不等于服务器历史审计；生产者还应保证同一事件的修订时间不倒退。
预期开始／结束时间可以在未来，不适用公告时间的「当前时间 + 5 分钟」上限。
时间信息不足时保留文字，不要把「稍后」编造成一个精确日期。

### 4.4 链接对象：Link

| 字段 | 类型 | 必填 | 限制与含义 |
| --- | --- | --- | --- |
| `sourceId` | string | 是 | 必须匹配 `sources[].id`。不接受不存在的来源。 |
| `label` | string | 是 | 最长 160；非空白，用于来源按钮。 |
| `url` | string | 是 | 符合第 2.4 节的 HTTPS URL 规则，优先使用原始公告链接。 |

接收端下载 Feed 时不会自动抓取这些链接。只有用户点击时才尝试用浏览器打开；
显示链接与域名不代表 EzMate 已核实该网页内容。

## 5. 状态与分类

### 5.1 status

| 值 | 含义 | 使用约定 |
| --- | --- | --- |
| `preview` | 提前预告 | 尚未宣布发放；可保留预计时间范围。 |
| `announced` | 已宣布发放／实施 | 只代表公开消息状态，不代表本机账户符合资格或已到账。 |
| `corrected` | 更正 | 保留同一 ID，提高 revision，并说明更正内容。 |
| `retracted` | 撤回 | 保留同一 ID，提高 revision，明确说明旧公告已撤回。 |

当前解析器只检查状态是否合法，没有强制状态转移图。生产者应维持合理生命周期。
删除事件不是撤回通知；要告知客户端撤回，必须发布 `retracted` 修订。

### 5.2 evidence

| 值 | 含义 | 接收端信任边界 |
| --- | --- | --- |
| `official` | 生产者标记为官方来源 | UI 标明是 Feed 的分类；接收端不独立验证作者身份。 |
| `corroborated` | 生产者标记为交叉核实 | 生产者负责判断是否有独立证据，不能把同一帖的转载算成独立核实。 |
| `community` | 社群回报，尚未核实 | 通知默认关闭；即使开启，也不自动请求账户刷新。 |

HTTPS 只满足传输要求，不自动建立来源可信度。当前没有 Feed 签名校验、官方域名白名单或
作者认证流程。使用自建／第三方源前，必须信任其分类、时间和内容。

### 5.3 impacts

| 值 | 含义 |
| --- | --- |
| `global` | 公开的直接额度恢复／全局重置消息，不保证所有用户都适用。 |
| `banked` | 可储存的 Reset 次数发放。 |
| `policyChange` | 额度政策变更，不应单独视为已经发放 Reset。 |

一个事件可以同时包含 `global` 和 `banked`。未知分类不能临时加入数组；
新增枚举或改变字段语义，需要配套升级接收端，而不是直接发布 v2 给当前客户端。

## 6. 身份、修订、快照和去重

**对外接口是完整快照，不是增量事件队列。** 接收端活动页显示最近一次有效 Feed 的事件，
不从多次响应拼接无限历史，也不会请求下一页。生产者应在最多 200 个事件的限制内保留所需历史。

同一消息的预告、正式发放、更正和撤回，优先使用同一个 `id` 及递增 `revision`。
客户端不做自然语言语义去重；不同 ID 即使标题相同，也会当成不同事件。
`relatedEventId` 当前只是可接收的关联元数据，不是去重键。

| 变化 | 当前处理 |
| --- | --- |
| 第一次成功读取，或无有效缓存 | 保存基准，不发历史通知，不请求账户刷新。 |
| Feed URL 改变或 `feedId` 改变 | 按新 Feed 建立静默基准；不要用这些变化来更新一条普通公告。 |
| 已处理 ID、相同 revision | 不重复通知。 |
| 已知 ID、revision 增大 | 作为新修订，再经过时间、健康度和通知设置判定。 |
| 已处理记录中的 revision 倒退 | 拒绝整份响应，保留上次有效数据。 |
| 同 feedId 的 `generatedAt` 倒退 | 拒绝整份响应；相等本身允许。 |
| 上次快照中同 ID、同 revision，但已知字段内容改变 | 拒绝整份响应。 |
| 事件从快照消失 | 活动页不再显示它；不会据此发撤回通知。 |

同修订内容比较基于反序列化后的已知模型字段。改 `summary`、`updatedAt`、链接、链接排列顺序、
impact 排列顺序等都应提高 `revision`；单纯 JSON 缩进／对象属性顺序不构成该模型内容变化。
被忽略的扩展字段不是当前比较的可靠契约。校验范围依赖本机上次快照和已处理记录，不是全量历史数据库。

**每轮心跳只更新 Feed 及来源状态，不要为未变化的事件刷新 `updatedAt` 或递增 revision。**
否则会制造重复提醒；只改时间却不增 revision 还会导致响应被拒绝。

本地最多保留 2,048 个已处理事件的修订记录，优先保留当前快照中的 ID，再按处理时间保留较新的项。
这是有界去重，不是永久全历史去重；被淘汰的旧 ID 再次出现时，仍会按时间等规则重新判断。

## 7. 通知资格与账户刷新

### 7.1 时间与健康度门槛

除首次基准外，对尚未处理的新事件／新修订使用客户端当前时间 `now` 判断：

```text
Feed 新鲜： now - generatedAt <= 1 小时
事件近期： now - updatedAt <= 24 小时
来源有效： 至少一个 links[].sourceId 对应的来源满足
           health == "healthy"
           且 now - lastSuccessfulCheckAt <= 1 小时
```

这些条件在解析器的合法时间校验之后应用；5 分钟未来容差不是发布者可以伪造时间的理由。
门槛是固定值，目前没有 Feed 字段可改变它们。并非要求所有来源都健康；一个事件至少有一个
关联来源满足条件即可。`corroborated` 的核实质量仍由生产者负责。

近期事件因 Feed 或来源不新鲜而未处理时，接收端暂不记为已处理，等待后续健康快照再判断。
恢复时事件若已超过 24 小时，则只记录、不补发通知。已经过期的历史事件仍可在快照内显示。

`preview` 若提供 `expectedEndAt` 且它严格早于 `now`，则记录但不通知。
当前没有「到了 expectedStartAt 自动提醒」或「预告时间到自动变 announced」的调度器。
正式发放需由生产者发布新的事件修订。

### 7.2 行为矩阵

以下均以「不是首次基准、尚未处理、事件近期、Feed 新鲜且关联来源健康」为前提。

| 事件 | 弹窗条件 | 请求既有用量刷新 |
| --- | --- | --- |
| 官方／交叉核实 `preview` | `notifyPreviews=true`，且预告未按结束时间过期 | 否 |
| 官方／交叉核实 `announced` 或 `corrected`，含 global／banked | `notifyAnnouncements=true` | 是；即使公告弹窗关闭也仍可请求读取 |
| 官方／交叉核实 `announced` 或 `corrected`，仅 policyChange | `notifyAnnouncements=true` | 否 |
| 官方／交叉核实 `retracted` | `notifyAnnouncements=true` | 否 |
| 社群 `preview` | 同时启用 notifyCommunity 和 notifyPreviews，且预告未过期 | 否 |
| 社群其他状态 | 同时启用 notifyCommunity 和 notifyAnnouncements | 否 |

关闭通知时，符合处理条件的事件仍会记录为已处理；后来开启通知不会重播同一修订。
关闭整个 Feed 接收功能则不轮询；重新开启且保有原缓存时，会对未处理的近期消息重新判断。

同批公告合并到一个系统通知，最多列出三条事件并提示其余数量。判定顺序按 `updatedAt` 升序，
活动页显示按更新时间降序。操作系统是否真正展示通知，不构成服务端可获取的送达确认。

### 7.3 账户与执行边界

`UsageRefreshRequested` 只是请求已有用量读取流程合并刷新；不执行 Reset 兑换，不直接启动／续跑 Goal。
用户已启用的既有 Goal 监控仍可在其自身确认实际额度后按原逻辑处理。

当前没有逐事件的到账归因、方案自动筛选、到账结果回传或账户级回执。`scope` 只是文本，
不会据此过滤通知；公告与账户数值变化不能直接建立因果关系。
`notifyAccountChanges` 控制的是独立的本机 Reset 次数增加通知，不是 Feed 发布者可以设置的字段。

### 7.4 送达语义

处理流程先保存本地快照／修订记录，再调用通知和用量刷新事件。如果恰在保存后退出或崩溃，
某次弹窗或刷新请求可能没有发出；重启不会重放已保存的同一修订。
因此不能承诺至少一次送达或 exactly-once；接收端也不向 Feed 服务发送 ACK。
事件只要仍在当前快照，就可以从活动页查看。

## 8. 发布端实现要求

建议每 5–10 分钟检查并重新生成一次快照；这是部署建议，不是客户端保证。
来源检查和缓存策略必须使 `generatedAt` 与有效来源检查时间保持在一小时门槛内。
不要只重写时间戳来掩盖收集任务失败。

发布流程应当是：获取公开消息，保留原始来源和公开时间，核实分类，按同一事实合并转载，
为发生变化的事件递增 revision，然后生成完整 Feed；只有通过字段与时序校验后才原子替换线上正文，
同时更新 ETag／Last-Modified。避免客户端读到写了一半的 JSON。

来源失败仍可发布结构有效的 Feed，明确该来源 degraded／unavailable 并保留上次成功时间。
服务器不能以「200、空 events、全部 healthy」伪装抓取失败。
纠错或回滚部署时，应发布更高 revision 的 corrected／retracted，而不是降低已有事件修订号。

`feedId` 和事件 ID 不应该包含账户标识；Feed 必须只含公开信息。服务器需要的 X 等来源密钥仅留在
服务器，不得写入 Feed、源链接或桌面配置。不要向生产用户发布用于测试的虚构 official 公告。

## 9. 可解析的响应示例

以下均为虚构联调数据。验证参考时间为 `2026-09-24T12:05:00Z`。
晚于示例日期测试时，必须用合理的当前时间替换相关时间；旧示例可通过结构校验，但不会通过新通知的时间门槛。

### 9.1 最小有效 Feed／首次基准

```json
{
  "schemaVersion": 1,
  "feedId": "ezmate-integration-demo",
  "generatedAt": "2026-09-24T12:00:00Z",
  "sources": [
    {
      "id": "demo-source",
      "name": "虚构联调来源",
      "health": "healthy",
      "lastSuccessfulCheckAt": "2026-09-24T12:00:00Z"
    }
  ],
  "events": []
}
```

### 9.2 在同一 Feed 加入预告

```json
{
  "schemaVersion": 1,
  "feedId": "ezmate-integration-demo",
  "generatedAt": "2026-09-24T12:05:00Z",
  "sources": [
    {
      "id": "demo-source",
      "name": "虚构联调来源",
      "health": "healthy",
      "lastSuccessfulCheckAt": "2026-09-24T12:05:00Z",
      "message": "仅供联调，不是真实官方消息。"
    }
  ],
  "events": [
    {
      "id": "demo-banked-20260924",
      "revision": 1,
      "title": "【联调示例】预计未来一小时发放 Banked Reset",
      "summary": "这是一条虚构预告，用于测试展示和通知，不代表任何真实账户将获得 Reset。",
      "status": "preview",
      "evidence": "official",
      "impacts": ["banked"],
      "publishedAt": "2026-09-24T12:01:00Z",
      "updatedAt": "2026-09-24T12:01:00Z",
      "expectedStartAt": "2026-09-24T12:01:00Z",
      "expectedEndAt": "2026-09-24T13:01:00Z",
      "timeDescription": "未来一小时；示例时间范围，不是保证倒数。",
      "scope": "仅测试环境；不适用于真实账户。",
      "relatedEventId": null,
      "links": [
        {
          "sourceId": "demo-source",
          "label": "虚构预告原文",
          "url": "https://example.com/demo/banked-reset"
        }
      ]
    }
  ]
}
```

先读 9.1、再读 9.2，满足开关与新鲜度条件时才能验证新增预告通知。
若第一次直接读 9.2，它会成为静默基准，**不弹窗是预期行为**。

### 9.3 同事件后续修订

继续使用 9.2 的完整快照结构，保持 `feedId`、事件 `id` 和 `publishedAt` 不变。
每次更新快照生成时间、真实来源检查时间与缓存标记；下表不是可单独上传的增量对象。

| 场景 | revision | status | 必须同步处理 |
| --- | --- | --- | --- |
| 正式宣布发放 | 2 | announced | 更新 title／summary／updatedAt，移除或调整不再适用的预告文字和时间范围。 |
| 修正适用范围 | 3 | corrected | 更新 scope、说明更正内容、更新 updatedAt。 |
| 撤回旧公告 | 4 | retracted | 明确撤回原因，更新 title／summary／updatedAt。 |

在隔离测试环境中，r2 和含 banked 的 r3 会请求用量读取；r4 不会。
如果只是抓取心跳，事件继续保留原 revision 和 updatedAt。

## 10. 本地设置、缓存与错误

### 10.1 设置不是 Feed 字段

设置与快照位于 `%APPDATA%\CodexUsageAssistant\ResetWatch\`，分别为 `settings.json` 和 `state.json`。
推荐通过 UI 修改；直接编辑前应退出程序。以下是默认设置，不是服务器应返回的响应：

```json
{
  "enabled": false,
  "feedUrl": "",
  "pollIntervalMinutes": 5,
  "notifyPreviews": true,
  "notifyAnnouncements": true,
  "notifyCommunity": false,
  "notifyAccountChanges": true
}
```

启用时 Feed URL 不能为空。即使 enabled=false，非空 URL 仍需合法；轮询间隔始终要求 5–60 的整数。
设置保存失败会保留原设置；快照写入失败不会作为一次可发通知的成功更新。
写入采用同目录临时文件写完后替换，不应把 `state.json` 当作供外部系统修改的公开接口。

### 10.2 退避

检查或保存失败后，下一轮等待时间为：

```text
min(60 分钟, 配置间隔 × 2^连续失败次数) + 随机 0–30 秒
```

默认间隔下，首次失败后约等 10 分钟，再依次 20、40、60 分钟。成功检查并保存后清零失败次数。
时间从该轮处理结束后计算，不是严格整点执行。手动检查、保存设置可提前触发一次检查。
结构合法但数据过期的响应会被保存并提示过期，不等同于网络失败，不会仅因过期触发上述退避。
手动和后台检查共用锁，已有检查进行时不会并行重复下载；应用退出会取消 HTTP 请求。

### 10.3 内部错误标记

以下是 `ResetWatchService.Error` 的本地状态，不是对外返回的 HTTP 错误码：

| 值 | 典型原因 | 客户端表现 |
| --- | --- | --- |
| null | 没有当前记录的检查错误 | 仍须分别检查 Feed 与来源是否过期。 |
| settings | 本地设置无法加载／校验失败 | 回到默认关闭配置。 |
| cache | 本地快照或修订记录无法加载 | 丢弃无效内存状态，下次有效结果建立静默基准。 |
| invalid | JSON／版本／字段错误、时间或修订倒退、无缓存却收到 304 | 保留上次有效状态，不发新通知。 |
| fetch | 网络、HTTP、超时或保存等其他可处理失败 | 保留上次有效状态，按退避重试。 |

某个事件或来源无效会使整份响应失败，不会仅跳过坏行。问题排查应从结构、时间、来源引用和
修订单调性入手。HTTP 200 只证明有响应；不等于事件被通知，也不等于账户收到 Reset。

## 11. 进程内 C# 接口

以下供接收端扩展或测试替身使用，不是网络 API。命名空间为 `CodexUsageAssistant.Services`。

```csharp
public interface IResetFeedClient
{
    Task<ResetFeedResponse> FetchAsync(
        Uri address, string? etag, DateTimeOffset? lastModified,
        CancellationToken token);
}

public sealed record ResetFeedResponse(
    ResetFeed? Feed,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    bool NotModified = false);
```

| 接口／成员 | 用途与注意事项 |
| --- | --- |
| `ResetFeedClient.Parse(byte[], DateTimeOffset)` | 按指定参考时间验证 JSON，适合合同测试；无网络请求。 |
| `ResetFeedClient.TryGetHttpsUri(...)` | URL 合法性检查，不等同于可信域名验证。 |
| `ResetWatchService.InitializeAsync(...)` | 加载设置和本地状态；不负责启动轮询。 |
| `ResetWatchService.StartAsync()` | 初始化并启动后台循环；功能关闭时等待配置变更。 |
| `ResetWatchService.RefreshAsync(...)` | 请求一次读取；功能关闭时不下载，已有检查时不启动并行读取。 |
| `ResetWatchService.SaveSettingsAsync(...)` | 校验并保存本地设置，唤醒后台循环。 |
| `ResetWatchService.Changed` | 状态更新事件；不保证在 UI 线程调用。 |
| `ResetWatchService.AnnouncementsAvailable` | 参数为本批可通知事件；不是已送达回执。 |
| `ResetWatchService.UsageRefreshRequested` | 无参数事件，只请求既有用量刷新。 |
| `ResetNotificationCoordinator.Evaluate(...)` | 纯通知策略计算，返回 Seen、Announcements 和 RefreshUsage。 |
| `IResetWatchStore` | LoadSettingsAsync／LoadStateAsync／SaveSettingsAsync／SaveStateAsync；可替换为测试存储。 |

WPF 调用方需要把后台事件调度到 Dispatcher，不应把网络完成回调当作 UI 线程。
账户读取继续由既有流程处理；不要在 Feed 解析器或通知策略中加入执行命令、兑换 Reset 或 Goal 操作。

## 12. 联调与验收

部署测试源后，在 EzMate「Reset 动态 → Feed 设置」填写最终 HTTPS 地址并启用。
使用隔离测试环境；公开分类为 official 的虚构 announced 消息可能触发本机用量读取，
不能发到生产 Feed 来测试。精简界面已移除「查看范例」「返回实际数据」「检查账户用量」入口，
范例 JSON 仍供解析器与自动化测试使用；界面简化不改变 Feed、通知或后台账户刷新协议。

| 测试 | 操作 | 预期 |
| --- | --- | --- |
| 首次基准 | 首次读最小有效 Feed | 显示正常状态，无历史弹窗。 |
| 新预告 | 同 feedId 加一个近期 preview | 按预告开关通知，不请求账户读取。 |
| 正式公告 | 同 ID revision+1，改 announced，含 banked | 按公告开关通知，并请求既有用量读取。 |
| 相同修订 | 重复下载，或重启后取得同修订 | 不重播。 |
| 非法内容修改 | 改 title 不改 revision | invalid，旧数据保留。 |
| 修订倒退 | 发布低于已处理记录的 revision | invalid。 |
| 来源失败 | 设 unavailable，保持旧成功时间 | 新近期事件暂缓；恢复且仍近期时再判断。 |
| 304 后过期 | 长期只返回同一旧 Feed | 下载检查可以成功，但 Feed／来源仍会过期，不发新通知。 |
| 社群默认关闭 | 新增 community 事件 | 动态页可见，不弹窗，不请求账户读取。 |
| 预告时间到了 | 不发布新修订，只等 expectedStartAt | 不会自动发正式公告或自动到账。 |
| 新 Feed 身份 | 更换 feedId 或配置新 URL | 静默重建基准，不重播历史。 |
| 输入限制 | 超大正文、错误 schema、坏来源引用、无时区日期 | 拒绝整份响应，不覆盖有效快照。 |
| 功能关闭 | 取消启用并保存 | 不继续轮询；本机账户通知的独立开关仍有效。 |

现有自动化测试入口（在仓库根目录运行，环境需具备项目所需 .NET／Windows 依赖）：

```powershell
dotnet test tests/CodexUsageAssistant.Tests/CodexUsageAssistant.Tests.csproj --filter "FullyQualifiedName~ResetFeedTests|FullyQualifiedName~ResetWatchTests|FullyQualifiedName~ResetWatchUiTests"
```

测试使用模拟 HTTP、测试存储及隔离测试数据，不需要真正的 X 来源或 Codex 账户。
本节给出复现入口，不把历史测试数量作为后续版本的验收保证。

## 13. 实现依据与交付边界

| 说明 | 工作区来源 |
| --- | --- |
| 数据类型和必填字段 | [ResetAnnouncement.cs](../src/CodexUsageAssistant.App/Models/ResetAnnouncement.cs) |
| HTTP、JSON、时间及大小校验 | [ResetFeedClient.cs](../src/CodexUsageAssistant.App/Services/ResetFeedClient.cs) |
| 生命周期、修订检查及错误处理 | [ResetWatchService.cs](../src/CodexUsageAssistant.App/Services/ResetWatchService.cs) |
| 新鲜度、通知和读取资格 | [ResetNotificationCoordinator.cs](../src/CodexUsageAssistant.App/Services/ResetNotificationCoordinator.cs) |
| 设置默认值与本地存储 | [ResetWatchSettings.cs](../src/CodexUsageAssistant.App/Models/ResetWatchSettings.cs)、[ResetWatchStore.cs](../src/CodexUsageAssistant.App/Services/ResetWatchStore.cs) |
| UI、通知及账户读取接线 | [ResetWatchViewModel.cs](../src/CodexUsageAssistant.App/ViewModels/ResetWatchViewModel.cs)、[TrayIconService.cs](../src/CodexUsageAssistant.App/Services/TrayIconService.cs)、[App.xaml.cs](../src/CodexUsageAssistant.App/App.xaml.cs) |
| 合同与行为测试 | [ResetFeedTests.cs](../tests/CodexUsageAssistant.Tests/Regression/ResetFeedTests.cs)、[ResetWatchTests.cs](../tests/CodexUsageAssistant.Tests/Regression/ResetWatchTests.cs) |
| 用户操作说明及内建范例 | [reset-watch.md](reset-watch.md)、[reset-feed.sample.json](reset-feed.sample.json) |

当前交付是接收端协议说明，不包含公告抓取后端、可用的正式 Feed、推送服务器、身份签名服务或
账户到账回传接口。后端只需先按本文提供完整 HTTPS Feed，即可与现有接收端联调；扩展这些能力时再单独版本化。
