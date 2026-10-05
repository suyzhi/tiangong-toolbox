# 联网授权（DEV 0.8）本机验证记录

日期：2026-09-29 21:10–21:25（+08:00）。验证对象：`origin/feature/online-activation` 的 `dc1ebc0`
「授权 DEV 0.8：联网激活 + 授权服务端 + 网页管理看板」（相对 `main` b149445：20 个文件、+3451 行）。

**结论：仓库里写的自测全部通过；另外补做了一轮"真插件 + 真 HTTP + 真服务端"的端到端验证，也全部通过。**
只有 1 处失败：服务端自测里的 `test_import_ledger_rows` 在 Windows 上退出时报临时目录清理错误（功能本身是对的，见发现 1）。

## 一、本机环境

| 项目 | 值 |
| --- | --- |
| 系统 | Windows x64，PowerShell |
| 天工 CAD 接口库 | `C:\Program Files\NDS\TianGong 2025\Program\TGAiHelper\Interop.TG.dll`（4,488,384 字节） |
| .NET Framework 编译器 | `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（build.ps1 用的那把） |
| .NET SDK | 10.0.202（跑 `tests/pure`） |
| 服务端 Python | 3.12.14 + cryptography 50.0.1（新建独立 venv：`C:\Users\admin\Documents\ChatGPT\.verify-venv-license`） |
| 验证用工作树 | 临时工作树（已删除，见文末） |

仓库里写明的验证入口：

- `DEV_0.7_授权/server/README.md` 第 127 行「自测」：`python3 -m unittest server/test_server.py`
- `DEV_0.7_授权/LICENSE.md` 第 160 行「6. 验证」：`make-test-codes.ps1` + `PanelTests.exe --license` / 全量 / `--autohole-ui`
- `LICENSE.md` 第 218 行「9. 发布检查清单」第 7、8 条：服务端上线、干净机器断网验证

## 二、按仓库文档跑的结果

| # | 命令 | 结果 |
| --- | --- | --- |
| 1 | `python -m unittest server/test_server.py -v` | 11 个用例：**10 通过 / 1 报错**（报错是 Windows 临时目录清理，见发现 1）；协议、配额、作废、看板鉴权/CSRF/限速/每日上限等断言全部通过 |
| 2 | `tools\LicenseAdmin\make-test-codes.ps1` | 生成 testmaster 测试密钥、签发三档真码（month 1eef-m267 / half aen9-h2g1 / year yg7v-r52n），开发构建成功 |
| 3 | `build\license-test\PanelTests.exe --license` | **128 项断言全过，退出码 0**（含 11 组联网用例：首激活必须联网、第二台机器换绑、服务器作废、7 天宽限期、伪造回执、0.7 老文件补登记） |
| 4 | `PanelTests.exe`（全量纯逻辑回归） | `CORE ASSERTIONS PASSED 142`、`AUTO-HOLE PURE ASSERTIONS 223`，退出码 0 |
| 5 | `PanelTests.exe --autohole-ui` | 30 项，退出码 0 |
| 6 | `tests\pure` → `dotnet run` | `ALL PURE SUITES PASSED`，退出码 0 |
| 7 | `build.ps1`（**正式构建**，不带 `-DevBuild`，用 keygen 生成的服务端槽） | 编译成功，输出「正式构建」；只有 `LicenseCodec.cs(73)` 一条既有告警 CS0675（不是本分支引入） |

> 说明：第 2–6 项跑的是提交里的**占位** `LicenseServerSlot.cs`；联网用例走的是测试钩子注入的进程内假服务器，所以占位不影响它们。

## 三、补做的端到端验证（真插件 ↔ 真 HTTP ↔ 真服务端）

仓库自测里插件侧用的是**进程内假服务器**（`tests/FakeLicenseServer.cs`），它没有覆盖 `LicenseOnline.Send()` 里真正的 HttpWebRequest 通路。所以我用服务端真实程序补了一轮：

```
python tg_license_server.py keygen        --url http://127.0.0.1:8750/api/v1/license   → server.key + LicenseServerSlot.cs
python tg_license_server.py keygen-master                                              → master.tgkey + LicenseKeySlot.cs + master.pub.json
copy 两个 *Slot.cs 覆盖到 src/License/  → tools/build.ps1 -DevBuild
python tg_license_server.py --db license.db serve --listen 127.0.0.1:8750 \
       --master master.pub.json --key server.key --signing-key master.tgkey --insecure-cookie
```

插件侧用一个 12 行的小宿主（必须以 `PanelTests` 为程序集名以复用 `InternalsVisibleTo`），**不注入任何测试桩**，
走真实机器指纹与真实 HTTP，然后逐条对照服务端 `list/show/events`：

| 场景 | 插件侧结果 | 服务端侧记录 |
| --- | --- | --- |
| 首次联网激活（管理员工具签发的真码） | `Valid`，365 天，提示"已绑定本机" | `activate ok bound 机器 643e657f40 IP 127.0.0.1` |
| 同码重复输入 | `Valid`，提示"已经在本机激活过了" | `activate ok already`，换机次数不增加 |
| 主动复核（`check`） | `Valid` | `check ok already` |
| 服务端 `revoke` 后复核 | `Revoked`，闸门关闭 | `check revoked` |
| 服务端 `unrevoke` 后复核 | `Valid`，闸门打开 | `check ok already` |
| 第二台电脑输同一个码 | `Valid` + "该码原先在另一台电脑上，已转到本机；剩余自助换机次数：1" | `activate ok rebound`，换机 1/2 |
| 换机次数用光后再换第三台 | 被拒："自助换机次数已用完（共 1 次）" | `activate quota` |
| A 机（码已被 B 机抢走）复核 | `MovedAway`，闸门关闭 | `check moved` |
| 断网时首次激活 | 被拒："联网激活失败（激活必须连上授权服务器）：无法连接授权服务器（ConnectFailure）"，**没有写激活文件**（状态仍为 `Missing`） | 无请求 |
| 同一端口换一把回执私钥冒充服务器 | 被拒："授权服务器的回执验签失败（服务器地址被劫持，或插件与服务器的密钥不配套）" | — |
| 网页看板登录 → 签发（`/admin/api/issue`）→ 插件激活该码 | `Valid`，30 天 | `activate ok bound` |
| 0.7 老激活文件（有码、无回执）联网 | 自动补登记，`Valid`，回执落盘 | `check ok already` ×2 |
| 0.7 老激活文件断网 | `OnlineRequired`："需要联网验证：本机已超过 7 天没有连上授权服务器…原因：无法连接授权服务器" | 无请求 |
| 提交里的**占位** `LicenseServerSlot`（地址为空） | 激活被拒："插件没有配置授权服务器地址（LicenseServerSlot 仍是占位）"——确认发不出去就没法用（fail-closed） | 无请求 |

服务端 `show` 输出（节选）：

```
0kb4-6y42  已绑定   一个月  到期 2026-10-29  机器 643e657f40  换机 0/2  最近 2026-09-29 21:24:07  看板签发
  2026-09-29 21:24:07  check    ok  already 机器 643e657f40  IP 127.0.0.1
  2026-09-29 21:21:49  activate ok  bound   机器 643e657f40  IP 127.0.0.1
```

## 四、发现

1. **服务端自测在 Windows 上有一个用例报错（功能正常，测试/CLI 资源未释放）**
   `test_import_ledger_rows`：导入本身成功（`导入完成：新增 8，已有记录补全 0，跳过 0`），失败发生在 `TemporaryDirectory` 退出时
   —— `[WinError 32] 另一个程序正在使用此文件: '…\\i.db'`。
   根因：`tg_license_server.py` 的 `main()` 调完 `a.func(a)` 就返回，`cmd_import_ledger` 里 `Ledger` 的 sqlite 连接从不关闭；
   进程内调用时连接还开着，Windows 不允许删除被占用的文件（Linux 允许，所以线上不会暴露）。
   最小复现：不关闭连接 → 清理失败；显式 `close()` → 清理成功。
   建议：`main()` 用 `try/finally` 关闭台账连接，或该用例改用 `TemporaryDirectory(ignore_cleanup_errors=True)`。
   在 Linux 服务器上跑（README 的目标环境）不受影响。
2. **文档与代码额度不一致（小）**：`LICENSE.md` 第 162 行仍写"授权链（95 项断言…）"，本机实跑是 **128 项**；第 6 节也没提新增的服务端自测，
   服务端自测只写在 `server/README.md`。建议把两处数字与入口对齐。
3. **宽限期到期时会在调用线程上同步联网（设计取舍，值得留意）**：`LicenseOnline.Evaluate` 在回执超过 7 天时会直接
   `Refresh(code,false)`，内部 `HttpWebRequest.Timeout = 8000`。CAD 里若在 UI 线程触发 `Current()`，最坏会卡约 8 秒。
   建议：宽限期到期走后台线程 + 明确提示，或把超时降到 3–4 秒。
4. **既有告警**：`src/License/LicenseCodec.cs(73)` 的 CS0675（"按位或/或运算扩展操作数"）在开发与正式构建里都出现，不是本分支引入的，
   但 `VALIDATION.md` 早期声称"无警告"，建议顺手改正。
5. **脚本小坑（不是分支缺陷）**：`build.ps1 -OutputDirectory <相对路径>` 用的是**进程**当前目录，不是 PowerShell 的 `Set-Location` 目录；
   我在临时工作树里用相对路径时，产物落到了主仓库的 `build\e2e`（已删除）。传绝对路径或按仓库脚本一样 `Join-Path $root` 就不会踩。

## 五、没有验证到的部分（残余风险）

- **HTTPS / Caddy 反向代理**：本轮是 `http://127.0.0.1:8750` 明文回环；证书、`--trust-proxy` 下的 X-Forwarded-For 取 IP 未实测。
- **systemd 部署**（`deploy/tg-license.service`）只在 Linux 服务器上有意义，本机没有跑。
- **需要真实时间的节奏**：24 小时复核、7 天宽限期、每日签发上限的跨天行为是用测试钩子改日期验证的，没有真的等一天。
- **CAD 里的人工操作**：`「重新联网验证」` 按钮是代码路径与单元测试级验证，没有用真鼠标在 CAD 界面里点一遍。
- **第二台物理机**：换机流程是用"改机器指纹"模拟的（与仓库自己的测试同法），没有第二台真机。

## 六、本机状态变更与恢复

- 授权单测会删除/重建本机激活文件与 `HKCU\Software\TianGongCadSuite`。动手前已备份，验证后**已恢复并核对 SHA-256 一致**：
  `activation.dat` → `31A260387610B365C9C4BD6CE6E1636FB0E6F62911E38B253D1C4589E1F0B94C`，注册表 `Fingerprint=cgz6-azt0`、`Counter=1`、`High=639262623511633354`。
  备份留在 `C:\Users\admin\Documents\ChatGPT\_verify_backup_license\`，确认无误后可删。
- 临时工作树、验证分支已删除；主仓库里没有留下我产生的改动。
- 验证用的脚手架与证据：`C:\Users\admin\Documents\ChatGPT\_verify_e2e\`（`VerifyE2E.cs` 宿主、`bin\`、两份 `*Slot.cs`、`server.key`/`master.tgkey`、`license.db`、服务端源码副本）。
  里面有私钥，**只在本机留档，别提交、别外发**。
- 主仓库工作区里原有的 `DEV_0.7_授权/tests/fixtures/license-test.ledger.tsv` 改动是本次验证之前就有的，我没有动它。

## 七、想重跑的话

```powershell
git fetch --all
git worktree add C:\somewhere\wt -b check/online origin/feature/online-activation
cd C:\somewhere\wt\DEV_0.7_授权
copy <本机已有的>tests\fixtures\license-test.tgkey .\tests\fixtures\   # 没有就会跳过验签用例（SKIP）
.\tools\LicenseAdmin\make-test-codes.ps1                              # 开发构建 + 三档真码
.\build\license-test\PanelTests.exe --license                          # 128 项
python -m unittest server/test_server.py                               # 服务端自测（Linux 上 11/11）
```
