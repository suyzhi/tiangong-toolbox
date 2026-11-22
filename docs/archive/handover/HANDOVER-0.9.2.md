# 0.9.2 交接（2026-10-25）：联网授权上线 + 自动更新

用户原话：「我想要有个网络认证，我可以随时取消激活和延长激活码」「再做一下自动更新的功能吧，就是我每次推送新版本，大家都可以收到更新通知，并选择下载安装这样的」
「用 cloudflare tunnel，你帮我自己去添加好 license.suyzhi.icu 域名」「换新的正式密钥，网页看板能签发激活码」

## 一、现网

| 项 | 值 |
| --- | --- |
| 地址 | `https://license.suyzhi.icu`（看板 `/admin/`，口令在 `文档\天工授权-正式密钥备份\看板口令.txt`） |
| 服务器 | VPS 69.63.223.0（Debian 12，root + 用户给的 ed25519 密钥），目录 `/opt/tg-license`，服务 `tg-license`（用户 tglicense，只听 127.0.0.1:8750） |
| 对外 | **独立的 Cloudflare Tunnel `tg-license`**（`/etc/cloudflared/tg-license.yml`，服务 `cloudflared-tg-license`）。443 归 s-ui（用户不想动），80/8443 归别的项目的 Caddy —— 都没碰；原有隧道 vpn-cdn（cdn.suyzhi.icu）也没碰 |
| 签发密钥 | 正式密钥 `suyzhi2026`：私钥 `/opt/tg-license/master.tgkey` + 离线备份 `文档\天工授权-正式密钥备份\master.tgkey`；公钥在 `src/License/LicenseKeySlot.cs` |
| 回执密钥 | `/opt/tg-license/server.key`（只在服务器上）；公钥 + 地址在 `src/License/LicenseServerSlot.cs` |

**坑**：`cloudflared tunnel route dns` 不带 `--config` 时用的是默认配置里的隧道（vpn-cdn），第一次把 license 的 CNAME 指错了，已用 `--config … --overwrite-dns` 改回。以后对这条隧道的操作都带 `--config /etc/cloudflared/tg-license.yml`。

## 二、联网授权（来自 feature/online-activation，合进 0.9.1 后补了「延长」）

* 首次激活必须联网；之后每天后台复核一次，断网 7 天内照常用。
* 看板：签发、作废 / 撤销作废、解绑、换机次数、**延长 / 缩短有效期（不用换码，0.9.1 新增）**、客户备注、联网记录。
* 换了正式密钥：**老码（测试密钥签的，含 yvz7259y、g303-3jw7、r13z-ssgy 等）在 0.9.2 上全部失效**。插件会提示「这是旧版本的激活码……请向管理员要新的激活码」。
* 已签发：本机一年码 3fbe-t6kq（已激活）、同事半年码 s92j-e898（仓库根目录 `发码-同事-半年-*.txt`）、联调码 kb4x-evbb（已作废）。

## 三、自动更新

* 服务器：`server/tg_license_update.py`。签名清单（回执私钥签）+ 安装包下载；看板「版本发布」上传；CLI `publish` / `releases`。
* 插件：`src/AutoUpdate.cs`、`UpdateForm.cs`、`UpdateModule.cs`。CAD 启动 20 秒后后台查（12 小时一次）；用户点工具箱命令时弹「发现新版本」（下载更新 / 稍后提醒 / 跳过这个版本）；功能区「检查更新」（命令 11，不过授权闸，GuiVersion 46）。下载校验大小 + SHA-256 → 解压 → WMI 起独立等待脚本，天工全部退出后跑安装包的 `install.ps1 -Force`，装完弹一句。
* **发版**：改 `VERSION` → `powershell -ExecutionPolicy Bypass -File DEV_0.7_授权\tools\publish-update.ps1 -Build -Notes "……"`（会问看板口令），或在看板上传 zip。

## 四、测试

* 服务端单测 17；插件授权 138（含延长 / 缩短 / 过期后延长）、更新 13（含窗口截图）；`--license-live`（本机服务器 9 步）、`--update-live`（7 步）。
* **现网端到端**：本机通过真实更新链路（license.suyzhi.icu → Cloudflare → 下载校验 → 等待脚本 → install.ps1）从 0.9.1 装上 0.9.2；
  联调码激活 → 看板 +30 天后本机剩 60 天 → 作废后再激活被拒；本机一年码激活 Valid。
* `make-test-codes.ps1` 现在用完会把 src 里的正式公钥槽放回去（以前会悄悄换成测试公钥）。

## 五、没做 / 注意

* 本地 `license-admin.ps1`（管理员工具）用的还是测试密钥 —— **正式码只在看板里签**。
* 作废 / 缩短最晚 1 天生效（`LicenseOnline.RefreshDays`），断网宽限 7 天（`GraceDays`）。
* 0.9.1 及以前装的同事：插件里没有自动更新，要手动装一次 0.9.2 安装包 + 新码；之后的版本就能自动更新了。
