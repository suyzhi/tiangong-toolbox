# 联网授权服务端（DEV 0.8）

插件激活时向这里登记"激活码 → 机器"，之后每天在后台复核一次。服务端做三件事：

1. **一码一机**：码已经绑在 A 机上，B 机再输同一个码 → 按"自助换机次数"决定能否转过去；次数用完就拒绝。
2. **即时作废**：`revoke` 之后，已激活的机器最迟在下一次复核（约 1 天内）时停用，不用再发新版本插件。
3. **签名回执**：服务端用自己的私钥给结论签名，插件只认内嵌公钥签过的回执——伪造服务器、改 hosts、中间人改包都没用。

另外带一个**网页管理看板**（`https://你的域名/admin/`）：签发激活码、查看谁在用、作废 / 解绑 / 调换机次数、改客户备注，手机也能用。

> **签发私钥放在服务器上**（按你选的方案）：看板口令就等于签发权限，服务器被攻破 = 别人能签任意激活码。
> 所以看板做了：口令 PBKDF2 存储、登录失败限速锁定、会话 12 小时过期、CSRF 校验、只允许同源脚本、每日签发上限、全部操作审计。
> 另外**强烈建议**在 Caddyfile 里把 `/admin` 限制成只有你自己的 IP 能访问（`deploy/Caddyfile` 里有现成的两行，去掉注释即可）。
> 不想让服务器签发，就不给 `--signing-key`、改用 `--admin`：看板照常管理，签发继续用本机的管理员工具。

## 一、准备服务器

- 一台 Linux 云服务器（1 核 1G 足够），Python 3.8+，一个域名。
- **国内（大陆）地域的服务器，域名要先做 ICP 备案**才能用 80/443 端口对外服务；不想备案就选**香港 / 新加坡**等地域。
- 域名 A 记录指向服务器公网 IP；安全组放行 80、443。

```bash
sudo useradd -r -m -d /opt/tg-license tglicense
sudo -u tglicense bash -c '
  cd /opt/tg-license
  python3 -m venv venv && venv/bin/pip install cryptography
'
# 把本目录的 tg_license_server.py 传到 /opt/tg-license/
```

## 二、生成密钥（只做一次）

在服务器上：

```bash
cd /opt/tg-license
sudo -u tglicense venv/bin/python tg_license_server.py keygen --url https://license.你的域名.com/api/v1/license
#  → server.key（服务端私钥，权限 600，只留在服务器上）
#  → LicenseServerSlot.cs（服务端公钥 + 地址，要编进插件）
```

激活码签发密钥（二选一）：

```bash
# ① 还没有生产签发密钥：直接在服务器上生成（推荐）
sudo -u tglicense venv/bin/python tg_license_server.py keygen-master
#  → master.tgkey（签发私钥，权限 600；再离线备份一份）
#  → LicenseKeySlot.cs（覆盖到插件 src/License/LicenseKeySlot.cs）
#  → master.pub.json（服务器验码用）

# ② 已经用管理员工具生成过：把 D:\keys\master.tgkey 传到 /opt/tg-license/（chmod 600），再导入公钥
sudo -u tglicense venv/bin/python tg_license_server.py import-master --slot LicenseKeySlot.cs
```

> 仓库里当前提交的 `LicenseKeySlot.cs` 是 **testmaster 测试公钥**，`import-master` 会拒绝它（联调时加 `--allow-test`）。
> **换签发密钥 = 已经发出去的所有激活码作废**，正式发码前定下来，之后别再动。

设置看板口令，并把以前用管理员工具发过的码导进来（可选，导入后能在看板里看到全文、客户备注）：

```bash
sudo -u tglicense venv/bin/python tg_license_server.py --db license.db set-password
sudo -u tglicense venv/bin/python tg_license_server.py --db license.db import-ledger master.ledger.tsv
```

## 三、启动服务

```bash
sudo cp deploy/tg-license.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now tg-license
# HTTPS：装 Caddy（自动申请证书），把 deploy/Caddyfile 里的域名改成你的
sudo cp deploy/Caddyfile /etc/caddy/Caddyfile && sudo systemctl reload caddy
curl https://license.你的域名.com/api/v1/health     # → {"ok": true, "day": ...}
```

服务只监听 `127.0.0.1:8750`，对外一律经 Caddy 的 HTTPS。

## 四、把服务端公钥编进插件

```
把服务器上生成的 LicenseServerSlot.cs 拷回来，覆盖 DEV_0.7_授权\src\License\LicenseServerSlot.cs
DEV_0.7_授权\tools\build.ps1          ← 正式构建
```

占位状态（地址为空）的插件**无法激活**——这是故意的，免得忘了配置就发出去一个"不联网也能用"的版本。

## 五、日常管理

**平时用看板就行**：浏览器打开 `https://你的域名/admin/`，输入口令。

- 左边**签发**：选档位（一个月 / 半年 / 一年）、数量、客户、备注 → 生成。结果可以逐个复制、全部复制，或下载成 txt 直接发给客户。
- 右边**列表**：按码ID / 客户 / 备注 / 机器搜索，按状态筛选（使用中、未激活、即将到期、已过期、已作废）。顶部卡片可以点，直接筛出对应的码。
- 点一行打开**详情**：激活码全文、绑定机器、每次激活 / 复核的记录（含 IP，被拒的会标红）、管理操作记录；在这里作废 / 撤销作废、解绑、改换机次数、改客户备注。

命令行也都还在（在服务器上执行）：

```bash
T="sudo -u tglicense /opt/tg-license/venv/bin/python /opt/tg-license/tg_license_server.py --db /opt/tg-license/license.db"
$T list                      # 所有码：状态、绑定机器、换机次数、最近联网时间
$T show abcd-efgh            # 一个码的详情 + 最近 20 次激活/复核记录（含 IP）
$T events --limit 100        # 全局最近记录：发现同一个码在不同 IP/机器间来回切，就是被外传了
$T revoke abcd-efgh --note "外传"   # 作废，约 1 天内全部机器停用；没见过的码也能提前作废
$T unrevoke abcd-efgh
$T unbind abcd-efgh          # 客户旧电脑坏了：解绑后新电脑直接激活，不占换机次数
$T quota abcd-efgh 5         # 单独调这个码的换机次数；--reset 把已用次数清零
```

默认每个码 2 次自助换机（`--default-quota` 改全局默认值）。

## 六、规则一览（插件侧）

| 场景 | 结果 |
| --- | --- |
| 首次激活 | **必须联网**；连不上就不写激活文件 |
| 同一台机器重复输码 | 不占次数，刷新回执 |
| 码在别的机器上，本机主动输码 | 有剩余次数 → 转到本机，原机器下次复核停用；没有 → 拒绝 |
| 已激活的机器日常使用 | 回执超过 1 天就在后台静默复核，不打扰用户 |
| 断网 | 7 天内照常用；超过 7 天必须联网一次（激活窗口里有「重新联网验证」按钮） |
| 服务器上作废 / 码被转走 | 下次复核后停用 |
| 把系统时间往回调 | 早于服务器上次回执日期一周以上 → 判定时间被回调 |
| 0.7 离线激活的老用户 | 升级后第一次联网自动补登记，不用重新输码 |

## 七、备份

台账就是 `license.db` 一个文件，定期拷走即可（`sqlite3 license.db ".backup license-$(date +%F).db"`）。
`server.key` 也要备份到**离线**位置——丢了就得重新 keygen、重新编译插件、所有客户升级。

## 自测

```bash
pip install cryptography
python3 -m unittest server/test_server.py
```
