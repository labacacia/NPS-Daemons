[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— NPS Daemons（套装）

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

在 NPS 达到 v1.0 稳定版之前，套件内所有仓库同步使用同一个预发布版本号。

---

## [1.0.0-alpha.18] —— 2026-08-15

### 变更

- 将四个公开 daemon image、包元数据和部署示例对齐到 alpha.18 同步候选版。

## [1.0.0-alpha.17] —— 2026-08-02

### 变更

- 将四个 bundle daemon 镜像、package metadata 与运维示例对齐到 alpha.17 服务端能力候选版。
- 在 runner/ingress publish overlay 中保留独立测试访问权限，并从 `npsd` 和 `nps-registry` 中移除存在漏洞的 SQLite runtime bundle。

## [1.0.0-alpha.16] —— 2026-07-23

### 变更

- 套件级 alpha.16 同步：在 alpha.15 已发布后，对齐包元数据、当前 README / 版本 banner、分发源树以及 release-prep 说明。
- 承载源事实树中的 nps-ingress 与 nps-runner 分发测试隔离修复。

## [1.0.0-alpha.15] —— 2026-06-28

### 变更

- 套件级 alpha.15 同步：对齐包元数据、当前 README / 版本 banner、分发源树以及 release-prep 说明到 NPS-Dev。
- 承载源事实树中的 NCP Tier-3 BinaryVector、入站 NWP Bridge server 加固、NIP canonical trust/revoke，以及 NDP discovery canonical-form 对齐。

## [1.0.0-alpha.14] —— 2026-06-26

- 套件版本同步到 1.0.0-alpha.14。

## [1.0.0-alpha.7] —— 2026-05-18

### 变更

- 所有 daemon 镜像标签、项目版本、publish-overlay 版本以及
  `LabAcacia.NPS.*` PackageReference 统一对齐到 `1.0.0-alpha.7`。

---

## [1.0.0-alpha.6] —— 2026-05-12

### 变更

- 所有 daemon 镜像标签、项目版本、publish-overlay 版本以及
  `LabAcacia.NPS.*` PackageReference 统一对齐到 `1.0.0-alpha.6`。

---

## [1.0.0-alpha.5] —— 2026-05-03

### 新增

- **原生 OS 安装包**：`.deb`（Ubuntu/Debian amd64）、`.rpm`（Fedora/RHEL x86_64）和
  `.msi`（Windows x64）安装包现已作为 GitHub Release Assets 与 Docker 镜像一同发布。
  每个安装包均包含自包含二进制，无需 .NET 运行时依赖。Linux 安装包注册 systemd service 单元文件；
  Windows MSI 通过 `NT SERVICE\<daemon>` 虚拟账户注册 Windows 服务。安装说明：
  参见 [NPS Wiki — Operator Quickstart Bundle](https://github.com/labacacia/NPS-Release/wiki/Operator-Quickstart-Bundle#option-b-native-packages-systemd--windows-service)。

### 变更

- **`npsd`** —— `AnchorNodeMiddleware` 增加 `topology:read` 能力门控
  和每事件 `cgn_est` 字段；未知保留 NWP 帧类型返回 HTTP 501 +
  `NWP-RESERVED-TYPE-UNSUPPORTED`。详见
  [`npsd/CHANGELOG.cn.md`](./npsd/CHANGELOG.cn.md)。
- **`nps-ingress`** —— `LabAcacia.NPS.NWP.Anchor` 升至
  `1.0.0-alpha.5`（wire 字段 `estimated_npt → cgn_est` 重命名）。
- **`nps-runner`** —— inbox watcher + worker spawn 完整实现（L3 FaaS
  runtime），替换 alpha.3/alpha.4 心跳骨架。详见
  [`nps-runner/CHANGELOG.cn.md`](./nps-runner/CHANGELOG.cn.md)。
- **`nps-registry`** / **`nps-cloud-ca`** —— 跟随套件版本，无功能变更。
- **`docker-compose.yml`** —— 所有镜像 tag 从 `1.0.0-alpha.4` 升至
  `1.0.0-alpha.5`。

完整套件级变更见
[NPS-Release CHANGELOG](https://gitee.com/labacacia/NPS-Release/blob/main/CHANGELOG.cn.md)。

---

## [1.0.0-alpha.4] —— 2026-04-30

### 新增

- **`npsd`** —— L1+ 层落地 sub-NID 签发 + per-NID inbox 队列。
  `POST/GET /v1/agents`、`GET/POST /v1/agents/{nid}`、
  `POST /v1/agents/{nid}/revoke` 由主机 root key 签出 IdentFrame，
  SQLite 存于 `${NPSD_DATA_DIR}/sub-nids.sqlite`。
  `POST /v1/inbox/{nid}`、`GET /v1/inbox/{nid}?wait=N&batch=B`
  （long-poll）、`DELETE /v1/inbox/{nid}/{message_id}`（ack）、
  `GET /v1/inbox/{nid}/depth` 提供内存队列，支持优先级、TTL、
  per-NID 深度上限、payload 大小上限。17 个集成测试在
  `NPS.Tests/Daemons/Npsd/`。
- **`nps-registry`** —— `SqliteNdpRegistry` 替换 alpha.3 stub。
  `Resolve` / `Graph` / `Announce` 全部走真实 SQLite 存储，TTL 懒过期。
  10 个集成测试覆盖注册、解析、图谱遍历、TTL 淘汰、并发写。

### 变更

- 4 个 daemon 的 `*.csproj` 全部升到 `1.0.0-alpha.4`，引用对应
  [`LabAcacia.NPS.*`](https://www.nuget.org/profiles/LabAcacia/) NuGet
  包的同版本号。
- 顶层 `docker-compose.yml` 的 `image:` tag 钉到
  `labacacia/<daemon>:1.0.0-alpha.4`。

### 跟随套件的协议变更

本次随套件 `v1.0.0-alpha.4` 同步：

- **NPS-RFC-0001 Phase 2** —— NCP 连接前导运行时帮助函数
  （preamble parser + writer；`npsd` 在线缆上仍是纯 HTTP，原生 preamble
  路由后续版本落地）。
- **NPS-RFC-0002 Phase A/B** —— `LabAcacia.NPS.NIP` 中的 X.509 NID
  证书 + ACME `agent-01`（`nps-cloud-ca` 和 `nip-ca-server` 后续会消费）。
- **NPS-CR-0002** —— Anchor Node 的 `topology.snapshot` /
  `topology.stream`（`nps-ingress` 等 Anchor 中间件落地后接入）。

仍为 Phase 1 骨架（自 alpha.3 无功能变更）：`nps-runner`、`nps-ingress`。

完整套件级汇总见
[`NPS-Release/CHANGELOG.cn.md`](https://gitee.com/labacacia/NPS-Release/blob/main/CHANGELOG.cn.md)。

---

## [1.0.0-alpha.3] —— 2026-04-26

### 新增

- 首次作为独立仓库
  [`labacacia/nps-daemons`](https://github.com/labacacia/nps-daemons)
  ([Gitee 镜像](https://gitee.com/labacacia/nps-daemons)) 发布。截至
  v1.0.0-alpha.2，这些 daemon 还不存在；alpha.3 在 dev 仓库引入作为
  套件部署面，本次首发即拆出独立仓。
- **`npsd`**（Layer 1，主机本地 NCP 接入 + 状态宿主）—— L1 最小集：
  绑定 `127.0.0.1:17433`，首次启动生成 root Ed25519 keypair（PKCS#8，
  POSIX 文件权限 `0600` —— 满足 NPS-Node Profile L1 合规用例
  `TC-N1-NIP-01`），暴露 `GET /health` 和 daemon 自身的 `GET /.nwm`。
- **`nps-runner`**（Layer 1，任务调度 / FaaS runtime）—— Phase 1
  骨架：Generic Host 脚手架 + 30 秒心跳。Inbox 监听 + `spawn_spec_ref`
  解析 + worker 生命周期在 L3 阶段（alpha.5+）。
- **`nps-ingress`**（Layer 2，公网 Internet ingress）—— Phase 1 骨架：
  `:8080` HTTP 监听 + `/health`，文档化里程碑。TLS 卸载、rate-limit、
  NeuronHub 鉴权、CGN 计费、NPS-RFC-0004 reputation 查询、
  NPS-CR-0001 Anchor Node 中间件接线在 alpha.4 → alpha.5。
- **`nps-registry`**（Layer 2，跨机 NDP 发现）—— Phase 1 骨架：
  在 NDP 可选专用端口 `17436` 监听；`Resolve` / `Graph` / `Announce`
  端点全部返回 `NDP-REGISTRY-UNAVAILABLE`（HTTP 503），方便消费者
  预先接线 + 优雅降级。SQLite 实仓在 alpha.4。
- 仓库 README（EN + CN）、顶层 `docker-compose.yml` 一键拉起 4 个
  daemon、仓库级 `LICENSE` + `NOTICE`、`docs/architecture.{md,cn.md}`
  完整三层拓扑文档（含 `innolotus/nps-cloud-ca` 和
  `innolotus/nps-ledger` 两个私有信任锚 daemon）。

### 变更

- 4 个 daemon 的 `*.csproj` 全部从仓内 `<ProjectReference>` 切换到
  nuget.org 上发布的
  [`LabAcacia.NPS.*`](https://www.nuget.org/profiles/LabAcacia/) NuGet
  包（v1.0.0-alpha.3）。发布仓现在脱离 monorepo 也能独立构建。
- Dockerfile 构建上下文从 monorepo 相对路径（`../../..`）改为仓库根，
  从发布仓直接进 daemon 子目录 `docker build .` 即可。

### 跟随套件的协议变更

本次随套件 `v1.0.0-alpha.3` 同步：

- **RFC-0001** —— NCP 连接前导（`npsd` 自身的 preamble runtime 在
  alpha.4 —— Phase 1 仅库层）。
- **RFC-0003** —— Agent 身份保证等级（涉及 NIP，以及 `npsd` 自身
  `/.nwm` 的 `min_assurance_level` 字段）。
- **RFC-0004** —— NID 声誉日志（Phase 1，仅 entry shape；in-memory
  实现在 `innolotus/nps-ledger` 而非本仓）。
- **CR-0001** —— Anchor / Bridge Node 拆分（`nps-ingress` 改用新的
  `LabAcacia.NPS.NWP.Anchor` 包）。

完整套件级汇总见
[`NPS-Release/CHANGELOG.cn.md`](https://gitee.com/labacacia/NPS-Release/blob/main/CHANGELOG.cn.md)。

---

[1.0.0-alpha.5]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
