[English Version](./CHANGELOG.md) | 中文版

# 变更日志 —— `nps-ingress`

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。Tag 跟随 NPS 套件统一 SemVer。

---

## [1.0.0-alpha.18] —— 2026-08-15

### 变更

- 将包元数据、runtime banner、publish-overlay SDK 引用与同步 daemon 列车对齐到 alpha.18 协议/SDK 候选版。

## [1.0.0-alpha.17] —— 2026-08-02

### 变更

- 对齐 package metadata、运行时版本 banner 与 publish overlay SDK 依赖，准备服务端能力对齐版本的 alpha.17 daemon 候选。

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

### 跟随套件

- 跟随 NPS 套件 `v1.0.0-alpha.7`。项目版本、publish-overlay 版本以及
  所有 `LabAcacia.NPS.*` PackageReference 已统一对齐到 alpha.7 发布线。

---

## [1.0.0-alpha.6] —— 2026-05-12

### 跟随套件

- 跟随 NPS 套件 `v1.0.0-alpha.6`。项目版本、publish-overlay 版本以及
  所有 `LabAcacia.NPS.*` PackageReference 已统一对齐到 alpha.6 发布线。

---

## [1.0.0-alpha.5] —— 2026-05-01

### 变更

- `LabAcacia.NPS.NWP.Anchor` 依赖升至 `1.0.0-alpha.5`。
  将拓扑事件的 wire 字段 `estimated_npt` 重命名为 `cgn_est`，以符合
  Cognon Budget 规范（NPS-5 §4.3 / NPS-AaaS §2.3）。
- ingress daemon 自身代码无变更；API 接口与路由行为与 alpha.4 完全一致。

---

## [1.0.0-alpha.4] —— 2026-04-30

### 跟随套件的协议变更

- `LabAcacia.NPS.*` NuGet 依赖升至 `v1.0.0-alpha.4`，其中新版
  `LabAcacia.NPS.NWP.Anchor` 包带来 **NPS-CR-0002** topology 查询类型
  （`topology.snapshot` / `topology.stream`）。本 daemon **暂未接线** ——
  Anchor 中间件集成仍是 alpha.4 → alpha.5 的工作。
- 自 alpha.3 无功能变更 —— 仍是 `:8080` HTTP 监听 + `/health` 骨架，
  TLS / rate-limit / 鉴权 / CGN 计费 / reputation 查询仍处于规划阶段。

---

## [1.0.0-alpha.3] —— 2026-04-26

### 新增

- 首次发布。NPS 套件的 Layer-2 公网 Internet ingress。
- Phase 1 骨架：`:8080` HTTP 监听 + `/health` 文档化里程碑，运维
  可以在 alpha.3 就把 nginx/Caddy/Traefik 接到它前面，alpha.4 → alpha.5
  只翻行为开关。
- 多阶段 Docker 镜像（非 root `npsing` 用户，暴露 `:8080`）。

### 推迟到 alpha.4 / alpha.5

- TLS 卸载（alpha.3 仅 HTTP，TLS 在上游处理）。
- Rate limit（按 NID / 按客户 / 按路由）。
- NeuronHub 客户鉴权 + 按客户触发 CGN 计费。
- 路由前的 NPS-RFC-0004 reputation 查询。
- `LabAcacia.NPS.NWP.Anchor` Anchor Node 中间件接线（NPS-CR-0001）。
- DDoS 防护（慢连接超时、请求频率上限、fail2ban 钩子）。

---

[1.0.0-alpha.5]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.5
[1.0.0-alpha.4]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.4
[1.0.0-alpha.3]: https://gitee.com/labacacia/nps-daemons/releases/tag/v1.0.0-alpha.3
