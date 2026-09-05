[English Version](./README.md) | 中文版

# NPS Daemons

[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](./LICENSE)
[![GitHub Release](https://img.shields.io/github/v/release/labacacia/NPS-Daemons?include_prereleases)](https://github.com/labacacia/NPS-Daemons/releases)
[![Architecture](https://img.shields.io/badge/architecture-3--layer-success)](./docs/architecture.cn.md)

**Neural Protocol Suite（NPS）** 的参考部署二进制 ——
覆盖 NPS 标准三层部署拓扑中**主机层 + 网络入口层** 4 个 OSS daemon。

> 源仓库：[gitee.com/labacacia/nps-daemons](https://gitee.com/labacacia/nps-daemons) ·
> GitHub 镜像：[github.com/labacacia/NPS-Daemons](https://github.com/labacacia/NPS-Daemons) ·
> 套件：[NPS-Release](https://gitee.com/labacacia/NPS-Release) ·
> 架构：[docs/architecture.cn.md](./docs/architecture.cn.md)

---

## 仓库内容

| 层 | Daemon | 默认端口 | alpha.19 技债收口候选状态 |
|----|--------|----------|------------------------|
| 1 | [`npsd`](./npsd/) | `127.0.0.1:17433` | 主机 root/sub-NID、有界 native NCP、持久 per-NID inbox、sub-NID 续租与签名 ephemeral NDP 在线广播；不宣称完整 L1。|
| 1 | [`nps-runner`](./nps-runner/) | —（worker）| Inbox 驱动的 portable OCI/legacy 执行，具备有界生命周期、持久 SQLite 租约、续租、旧属主围栏和终态去重；不宣称完整 TaskFrame DAG/Saga L3。|
| 2 | [`nps-ingress`](./nps-ingress/) | `:8080` 健康面，`:17443` native | TLS 1.3 + ALPN `nps/1.0`、默认开启 mTLS、session-NID 绑定、有界准入与本机 NCP 代理；明确不包含产品/AaaS 控制。|
| 2 | [`nps-registry`](./nps-registry/) | `:17436` | SQLite Announce/Resolve/Graph、TTL 过期、单调 graph sequence、多 Anchor epoch 处理与有界 federation 防环。|

每个 daemon 在自己的子目录里有独立的 `Dockerfile` / `docker-compose.yml` /
README —— 共享发布节奏、共享基础镜像，但独立构建独立发版。

### 不在本仓的部分

NPS 三层中的**信任锚 / 云** daemon 由独立的 LabAcacia 仓库维护，
不会物化进本四-daemon bundle：

- [`labacacia/NPS-Cloud-CA`](https://github.com/labacacia/NPS-Cloud-CA) —— 跨组织 NID 证书颁发机构 + CRL/OCSP。
- [`labacacia/NPS-Ledger`](https://github.com/labacacia/NPS-Ledger) —— 实现 [NPS-RFC-0004](https://gitee.com/labacacia/NPS-Release/blob/main/spec/rfcs/NPS-RFC-0004-nid-reputation-log.md)
  的 Certificate-Transparency 风格 NID 声誉日志。

**今天**就要自托管 CA 的话用 [`labacacia/NIP-CA-Server`](https://gitee.com/labacacia/nip-ca-server)
—— 单组织 OSS CA。

---

## 快速开始（4 个 daemon 一起拉起）

```bash
git clone https://gitee.com/labacacia/nps-daemons.git
cd nps-daemons
docker compose up -d

# 各 daemon 的 /health 在各自端口
curl -s http://localhost:17433/health   # npsd
curl -s http://localhost:8080/health    # nps-ingress
curl -s http://localhost:17436/health   # nps-registry

docker compose logs -f nps-runner       # nps-runner 无 HTTP 接口
```

或者只拉一个：

```bash
docker compose up -d npsd
```

## 快速开始（单 daemon，不用 compose）

每个子目录都有自包含的 Dockerfile：

```bash
cd npsd
docker build -t labacacia/npsd:1.0.0-alpha.19 .
docker run --rm -p 17433:17433 -v npsd-data:/data labacacia/npsd:1.0.0-alpha.19
```

源码构建也行（需要 .NET 10 SDK）：

```bash
cd npsd
dotnet restore
dotnet run
```

所有 daemon 依赖 nuget.org 上发布的 `LabAcacia.NPS.*` NuGet 包
（`Core`、`NIP`、`NDP`、`NWP`、`NWP.Anchor`、`NOP`），不依赖 monorepo。

## 原生安装包（不需要 Docker）

[GitHub Release](https://github.com/labacacia/NPS-Daemons/releases) 随 Docker 镜像一同发布自包含原生安装包 ——
无需 .NET 运行时，开箱即用。Linux 安装包注册 systemd 服务；Windows MSI 通过
`NT SERVICE\<daemon>` 虚拟账户注册 Windows 服务。

版本号 `1.0.0-alpha.19` 替换为当前发布版本即可。

### Ubuntu / Debian（amd64）

```bash
VER=1.0.0-alpha.19
for pkg in npsd nps-runner nps-ingress nps-registry; do
    curl -LO "https://github.com/labacacia/NPS-Daemons/releases/download/v${VER}/${pkg}_${VER//-alpha./~alpha.}_amd64.deb"
    sudo dpkg -i "${pkg}_${VER//-alpha./~alpha.}_amd64.deb"
done
```

也可以只安装需要的 daemon，例如：

```bash
VER=1.0.0~alpha.13   # Debian 版本格式（用 ~ 替换 -）
curl -LO "https://github.com/labacacia/nps-daemons/releases/download/v1.0.0-alpha.19/npsd_${VER}_amd64.deb"
sudo dpkg -i "npsd_${VER}_amd64.deb"
sudo systemctl status npsd
```

配置覆盖文件（升级时保留）：`/etc/nps/<daemon>/env`

数据目录：`/var/lib/nps/<daemon>/`（系统用户 `npsd` 等专属账户所有）

### Fedora / RHEL（x86_64）

```bash
VER=1.0.0-alpha.19
RPM_VER=1.0.0
RPM_REL=0.alpha.6.1
for pkg in npsd nps-runner nps-ingress nps-registry; do
    curl -LO "https://github.com/labacacia/NPS-Daemons/releases/download/v${VER}/${pkg}-${RPM_VER}-${RPM_REL}.x86_64.rpm"
    sudo rpm -i "${pkg}-${RPM_VER}-${RPM_REL}.x86_64.rpm"
done
```

配置覆盖文件：`/etc/nps/<daemon>/env`

数据目录：`/var/lib/nps/<daemon>/`

### Windows（x64，MSI）

```powershell
$ver = "1.0.0-alpha.19"
foreach ($pkg in @("npsd","nps-runner","nps-ingress","nps-registry")) {
    $file = "$pkg-$ver-win-x64.msi"
    Invoke-WebRequest -Uri "https://github.com/labacacia/NPS-Daemons/releases/download/v$ver/$file" -OutFile $file
    Start-Process msiexec.exe -ArgumentList "/i $file /quiet /norestart" -Wait
}
# 服务自动启动，验证：
Get-Service npsd, nps-runner, nps-ingress, nps-registry
```

安装路径：`%ProgramFiles%\LabAcacia\<daemon>\`

数据目录：`%ProgramData%\LabAcacia\<daemon>\`

### 卸载

```bash
# Debian/Ubuntu
sudo apt remove npsd nps-runner nps-ingress nps-registry

# Fedora/RHEL
sudo rpm -e npsd nps-runner nps-ingress nps-registry
```

```powershell
# Windows
foreach ($pkg in @("npsd","nps-runner","nps-ingress","nps-registry")) {
    Get-Package $pkg | Uninstall-Package
}
```

## 架构

完整的三层参考拓扑（含 2 个独立维护的信任锚 daemon）见
[`docs/architecture.cn.md`](./docs/architecture.cn.md)。简版：

```
┌─────────────────────────────────────────────────────────┐
│ Layer 3（独立的 LabAcacia 仓库）                        │
│   nps-cloud-ca · nps-ledger                             │
├─────────────────────────────────────────────────────────┤
│ Layer 2（本仓库）—— 网络入口                            │
│   nps-ingress（公网 ingress）· nps-registry（NDP）       │
├─────────────────────────────────────────────────────────┤
│ Layer 1（本仓库）—— 主机本地                            │
│   npsd（状态宿主，:17433）· nps-runner（FaaS）           │
└─────────────────────────────────────────────────────────┘
```

## 规范 & SDK 参考

- [NPS-Release](https://gitee.com/labacacia/NPS-Release) —— 协议规范。
- [NPS-Node Profile](https://gitee.com/labacacia/NPS-Release/blob/main/spec/services/NPS-Node-Profile.cn.md) —— `npsd` 对照构建的合规规范。
- [NPS-Node-L1 合规](https://gitee.com/labacacia/NPS-Release/blob/main/spec/services/conformance/NPS-Node-L1.cn.md) —— 20 个 `TC-N1-*` 用例。
- [NPS-sdk-dotnet](https://gitee.com/labacacia/NPS-sdk-dotnet) —— daemon 依赖的 .NET SDK。
- [labacacia/nip-ca-server](https://gitee.com/labacacia/nip-ca-server) —— 当下用于真签发的单组织 OSS CA。

## 版本

跟随 NPS 套件统一 SemVer。NPS 1.0 之前所有组件用同一 `1.0.0-alpha.x` tag。
逐 daemon 版本说明见各子目录的 `CHANGELOG.cn.md`；汇总版本说明在仓库
根目录 [`CHANGELOG.cn.md`](./CHANGELOG.cn.md)。

## 许可证

Apache License 2.0，见 [`LICENSE`](./LICENSE) 和 [`NOTICE`](./NOTICE)。

版权所有 © 2026 LabAcacia（INNO LOTUS PTY LTD）。
