# 授权与防盗版（DEV 0.7）

插件从 DEV 0.7 起**必须激活才能使用**。本版把方案改成：

> **管理员只发激活码；用户在自己的电脑上激活时，激活码自动绑定这台机器。**

用户**不需要**上报机器码，管理员**不需要**提前知道机器码。激活码只有持有管理员私钥的人能签发，
有效期分**一个月 / 半年 / 一年**三档。

---

## 1. 发给用户（三步）

1. 用户照常安装插件（安装包里的 安装.cmd）。
2. 启动天工 CAD，点任意插件命令 → 弹出「天工工具箱 授权激活」窗口。
3. 把管理员发来的激活码**整段粘贴**进输入框 → 点「激活」。
   成功后提示「激活成功，该激活码已绑定本机（码ID xxxx-xxxx）」，本机以后不再需要输入。

粘贴时不用管大小写、空格和换行，分隔符会忽略；O/o 与 0、I/i/l 与 1 按常见写法自动纠正；
抄错会提示「格式不正确」，而不是「验签失败」。窗口里的「本机机器码」只在售后核对时用得上，
**管理员发码时不需要它**。

激活状态落盘位置（用户不需要关心）：

    %ProgramData%\TianGongCadSuite\activation.dat      DPAPI（机器范围）加密，拷到别的机器解不开
    %LOCALAPPDATA%\TianGongCadSuite\activation.dat.bak  影子副本
    HKCU\Software\TianGongCadSuite                      单调时间基准与激活计数器

---

## 2. 管理员：签发激活码

管理员工具是**独立程序**，不随插件分发给客户：tools/LicenseAdmin/build/TianGongLicenseAdmin.exe

    0) 编译管理员工具
       DEV_0.7_授权\tools\LicenseAdmin\build-admin.ps1

    1) 生成密钥对（只做一次）
       TianGongLicenseAdmin.exe keygen D:\keys\master.tgkey master
       私钥写到 master.tgkey，公钥源码写到 master.public.cs

    2) 把公钥源码覆盖到插件里，然后重新编译插件
       copy D:\keys\master.public.cs DEV_0.7_授权\src\License\LicenseKeySlot.cs
       DEV_0.7_授权\tools\build.ps1

    3) 签发（不需要机器码；M=一个月 H=半年 Y=一年）
       TianGongLicenseAdmin.exe new D:\keys\master.tgkey Y --note "XX 公司 王工"
       → 打印 码ID（形如 abcd-efgh）、签发日、到期日、激活码全文

    4) 签发多个
       TianGongLicenseAdmin.exe batch D:\keys\master.tgkey M 10 --note "试用批次 A"

命令一览（TianGongLicenseAdmin.exe help）：

| 命令 | 作用 |
| --- | --- |
| keygen <私钥> [密钥ID] | 生成密钥对，写出私钥与公钥源码 |
| new <私钥> <档位> [YYYY-MM-DD] [--note 备注] | 签发一个激活码并记入台账（**不需要机器码**） |
| new <私钥> <档位> --bind <机器码> | 仍然支持签成"指定机器"的码（老方案按需用） |
| batch <私钥> <档位> <数量> [YYYY-MM-DD] | 一次签发多个 |
| list <私钥> [issued\|activated\|void] | 列出台账与三态统计 |
| mark <私钥> <码ID或激活码> <issued\|activated\|void> | 登记"发给了谁 / 客户已激活 / 作废" |
| revoke <私钥> <码ID或激活码或@文件> | 作废该码，并重新生成插件里的黑名单 |
| export <私钥> [输出路径] | 只按台账重新生成插件里的黑名单 |
| verify <激活码或@文件> | 校验一个码并显示明细（码ID、档位、签发/到期） |
| verify <私钥> <激活码> | 连台账状态一起显示（是否已作废） |
| machine / plans / public | 自查机器码 / 列档位 / 打印公钥坐标 |

### 私钥保管（最重要的一条）

- 私钥文件（*.tgkey）**只在管理员离线机器上**保存，绝不进代码仓库、绝不发给客户。
- 私钥泄露 = 授权体系失效：任何人都能签发任意时长的激活码。
- 换私钥会让**已发出的所有激活码作废**，必须给存量客户重发。正式发布前先把密钥定下来。
- 仓库里 src/License/LicenseKeySlot.cs 默认是**占位公钥**（全零）：这样编译出来的插件能装能跑，
  但**任何激活码都过不了验签**，必须先做第 2 步。
- 本机联调可以跑 tools/LicenseAdmin/make-test-codes.ps1：它生成一对**测试密钥**写进公钥槽并重新编译，
  再签发三档真码供测试。**发布前务必把公钥槽换回生产公钥**（占位或测试公钥发出去等于没有授权）。

---

## 3. 管理员：台账与作废

台账文件与私钥同名同目录：<私钥名>.ledger.tsv，只在管理员机器上，**别提交、别外发**。
一行一个码，状态三态：

| 状态 | 含义 |
| --- | --- |
| issued | 已签发（码发给客户了，还没回报激活结果） |
| activated | 客户已回报激活（**管理员主动问一句"激活了吗"，然后 mark 一下**） |
| void | 已作废（黑名单会随下一个版本下发，该码在任何机器上都不能用） |

典型流程：

    TianGongLicenseAdmin.exe new    D:\keys\master.tgkey Y --note "XX 公司"
    ...把码发给客户，客户在自己机器上激活...
    TianGongLicenseAdmin.exe mark   D:\keys\master.tgkey abcd-efgh activated --machine "cgz6-azt0-fzkr-cykf"
    ...发现该码被外传/需要停止授权...
    TianGongLicenseAdmin.exe revoke D:\keys\master.tgkey abcd-efgh --note "泄漏，作废"
    → 自动改写插件源码 src/License/LicenseRevoked.cs，然后**重新编译插件并把新版本发给客户**

黑名单文件长这样（只写码ID，不写完整码）：

    internal static readonly string[] GeneratedIds = new string[]{
        "thpgt5wf", // thpg-t5wf 泄漏，作废
    };

---

## 4. 有效期与三档

| 档位 | 标识 | 天数 | 说明 |
| --- | --- | --- | --- |
| 一个月 | M | 30 天 | 短期试用 / 单项目 |
| 半年 | H | 183 天 | 常规采购 |
| 一年 | Y | 365 天 | 年费 |

- 有效期以**签发日**为起点，不是激活日。所以发给客户的码最好当天签发；
  new 命令支持可选参数指定签发日（YYYY-MM-DD），补发历史授权时用得上。
- 剩余 14 天以内开始提醒；到期当天立即失效并弹激活窗口。
- 续期就是再输一个新码：新码的档位与到期日直接覆盖旧的，不需要先卸载。

---

## 5. 能做到什么、做不到什么（请照实告知客户）

| 场景 | 结果 |
| --- | --- |
| 同一台机器重复输入同一个码 | 提示「已经在本机激活过了」，不重复计数，不报错 |
| 把激活文件拷到另一台机器 | 用不了：DPAPI 机器密钥不同，解不开；指纹也对不上 |
| 换了主板/硬盘（机器指纹变了） | 本机激活失效，需要管理员重新发码 |
| **同一个码在第二台机器上重新激活** | **离线时无法察觉**：没有联网心跳，本机不可能知道这个码在别处用过 |
| 该码被管理员作废之后 | 装了带这份黑名单的版本就拒绝：未激活的机器不能用；已经激活的机器下次校验也失效 |
| 已经发出去、还没更新的旧版本插件 | **无法即时吊销**，要等客户换成新版本 |

> 结论：这套方案是**离线一码一机 + 管理员台账 + 版本化吊销**。
> 如果你需要「同一个码换机必失效、且即时生效」，只有加一个可访问的登记点（公网或内网 HTTP 服务，
> 首次激活时登记「码 → 机器」，第二台机器用同码直接被拒）。当前版本没有服务端，故不提供该能力。

---

## 6. 验证

    # 授权链（95 项断言：格式往返、抄写校验、篡改检测、三档天数、到期、机器绑定、激活即绑定、
    # 重复激活幂等、续期、时间回调容差、状态重置、作废清单、以及"管理员工具签发的真码能被插件接受"）
    DEV_0.7_授权\tools\LicenseAdmin\make-test-codes.ps1
    DEV_0.7_授权\build\license-test\PanelTests.exe --license

    # 完整回归（授权 + 几何 + 自动打孔 + 孔型预览 UI）
    DEV_0.7_授权\build\license-test\PanelTests.exe
    DEV_0.7_授权\build\license-test\PanelTests.exe --autohole-ui

真机鼠标实测（不是 COM 快捷调用）的记录、截图与发现见 [MOUSE-E2E-20260926.md](MOUSE-E2E-20260926.md)。

---

## 7. 代码结构

| 文件 | 作用 |
| --- | --- |
| src/License/LicensePayload.cs | 27 字节签名载荷格式（掩码/档位/指纹/签发日/随机数） |
| src/License/LicensePlans.cs | 三档定义（30/183/365 天，编号 11/27/42） |
| src/License/LicenseCodec.cs | 激活码编解码（32 字符表、每字符 5 bit、抄写校验、码ID） |
| src/License/LicenseSignature.cs | ECDSA-P256 验签与签发 |
| src/License/LicenseKeySlot.cs | 公钥槽（由 keygen 覆盖；默认占位） |
| src/License/LicenseKeyMaterial.cs | 公钥去掩码与 CNG BLOB 组装 |
| src/License/LicenseMachine.cs | 机器指纹与机器码 |
| src/License/LicenseStore.cs | 激活文件落盘、DPAPI、单调时间基准 |
| src/License/LicenseRevoked.cs | **作废码黑名单**（默认空表，由管理员工具 revoke/export 生成） |
| src/License/LicenseLibrary.cs | 授权链主流程（校验顺序、激活即绑定、令牌、闸门、作废判定） |
| src/License/LicenseGuard.cs | 环境自检与完整性自检 |
| src/License/LicenseGate.cs | 业务栅栏（交互式与静默两种） |
| src/License/LicenseUi.cs | 激活窗口（粘贴码 → 激活即绑定） |
| src/Admin/LicenseAdminKey.cs | 管理员侧密钥读写与签发（**不进插件 DLL**） |
| src/Admin/LicenseLedger.cs | 管理员台账与黑名单源码生成（**不进插件 DLL**） |
| src/Admin/LicenseAdminMain.cs | 管理员命令行入口（**不进插件 DLL**） |

---

## 8. 激活码格式（实现说明）

    载荷 27 字节：掩码(1) + 'T''G'(2) + 版本(1) + 档位(1) + 机器指纹(10) + 签发日(4,大端) + 随机数(8)
    签名 64 字节：ECDSA-P256 / SHA-256（IEEE P1363）
    正文 91 字节：每 5 字节 → 8 个字符（每字符 5 bit），19 组
    校验 1 组 8 字符：正文 HMAC-SHA256 的前 5 字节
    合计 20 组 = 160 字符，显示时按 5 字符分段，共 191 个可见字符（含 31 个 '-'）

字符表 0123456789abcdefghjkmnpqrstvwxyz（32 个，去掉 i l o），大小写不敏感。

- 未绑定的通用码：掩码位 bit0=0，指纹字段填随机数（无意义），**激活时才绑定**。
- 指定机器的码：bit0=1，指纹字段填该机短指纹（老方案）。
- **码ID**：载荷 SHA-256 的前 5 字节按同一字母表编成 8 个字符，显示成 abcd-efgh。
  台账与黑名单都用它；载荷里带 8 字节随机数，所以同档位、同一天、同一台机器签发的码也是不同 ID。

---

## 9. 发布检查清单

1. keygen 生成**生产**密钥对，公钥覆盖 LicenseKeySlot.cs，重新编译。
2. 私钥离线保管，确认没有进仓库（git status 里不应出现 *.tgkey）。
3. 确认 src/License/LicenseRevoked.cs 是当前台账导出的黑名单（revoke 之后必须 export + 重编译）。
4. 跑 tools/build.ps1 与 PanelTests.exe（全量 + --license）确认全绿。
5. 在干净机器上装一次，点命令走完"弹窗 → 粘贴码 → 激活 → 命令可用"。
6. 台账 <私钥名>.ledger.tsv 不要随安装包发出去。
