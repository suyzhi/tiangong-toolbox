# 授权与防盗版（DEV 0.7 新增）

插件从 DEV 0.7 起**必须激活才能使用**。没有激活码时，所有命令都会弹出激活窗口；
激活码只有管理员能签发（需要管理员私钥），有效期分**一个月 / 半年 / 一年**三档，
并且**绑定机器**：同一个激活码换到别的机器上无法使用。

## 1. 发给用户

1. 用户照常安装插件（`交付_DEV_0.7.0_*` 里的安装包）。
2. 启动天工 CAD，点任意插件命令 → 弹出激活窗口。
3. 窗口第一行显示**机器码**（形如 `cgz6-azt0-fzkr-cykf`）。让用户点"复制机器码"发给管理员。
4. 管理员签发激活码后发回，用户整段粘贴进第二个输入框 → 点"激活"。
5. 成功后状态栏显示剩余天数；同一台机器以后不再需要输入。

粘贴时不用管大小写、空格和换行，分隔符 `-` 会自动忽略；`O/o` 与 `0`、`I/i/l` 与 `1`
会按常见写法自动纠正。激活码本身带抄写校验，抄错会直接提示"格式不正确"而不是"验签失败"。

激活状态落盘位置（用户不需要关心）：

- `%ProgramData%\TianGongCadSuite\activation.dat`：DPAPI（机器范围）加密，复制到别的机器无法解密
- `%LOCALAPPDATA%\TianGongCadSuite\activation.dat.bak`：同一份内容的影子副本
- `HKCU\Software\TianGongCadSuite`：单调时间基准与激活计数器

## 2. 管理员：签发激活码

管理员工具是**独立程序**，不随插件分发给客户：`tools/LicenseAdmin/build/TianGongLicenseAdmin.exe`。
首次使用需要先生成密钥对。

```powershell
# 0) 编译管理员工具
DEV_0.7_授权\tools\LicenseAdmin\build-admin.ps1

# 1) 生成密钥对（只做一次）
#    私钥写到 master.tgkey，公钥源码写到 master.public.cs
TianGongLicenseAdmin.exe keygen D:\keys\master.tgkey master

# 2) 把公钥源码覆盖到插件里，然后重新编译插件
copy D:\keys\master.public.cs DEV_0.7_授权\src\License\LicenseKeySlot.cs
DEV_0.7_授权\tools\build.ps1

# 3) 按用户报来的机器码签发（M=一个月 H=半年 Y=一年）
TianGongLicenseAdmin.exe new D:\keys\master.tgkey M cgz6-azt0-fzkr-cykf

# 一次签发多个（同一档位）
TianGongLicenseAdmin.exe batch D:\keys\master.tgkey Y cgz6-azt0-fzkr-cykf,abcd-1234-efgh-5678

# 校验一个激活码（看档位、签发日、到期日、绑定机器、签名是否通过）
TianGongLicenseAdmin.exe verify "@D:\code.txt"
```

命令一览（`TianGongLicenseAdmin.exe help`）：

| 命令 | 作用 |
| --- | --- |
| `keygen <私钥文件> [密钥ID]` | 生成密钥对，写出私钥与公钥源码 |
| `new <私钥> <档位> <机器码> [签发日]` | 签发一个绑定该机器的激活码 |
| `batch <私钥> <档位> <机器码列表>` | 批量签发（逗号分隔） |
| `verify <激活码\|@文件>` | 校验并打印明细，签名不过返回码 9 |
| `machine` | 显示本机机器码（管理员自查） |
| `public <私钥>` | 打印公钥坐标 |
| `plans` | 列出三档天数 |

### 私钥保管（最重要的一条）

- 私钥文件（`*.tgkey`）**只在管理员离线机器上**保存，绝不进代码仓库、绝不发给客户。
- 私钥泄露等于授权体系失效：任何人都能签发任意机器、任意时长的激活码。
- 换私钥（重新 keygen 并覆盖 `LicenseKeySlot.cs` 重编译）会让**已发出的所有激活码作废**，
  必须同时给所有存量客户重发激活码。所以正式发布前先把密钥定下来。
- 仓库里 `src/License/LicenseKeySlot.cs` 默认是**占位公钥**（全零）。用占位公钥编译出来的
  插件能装能跑，但**任何激活码都过不了验签**，必须先做上面第 2 步。
- 本机联调如果只想跑通流程，可以运行 `tools/LicenseAdmin/make-test-codes.ps1`：它会生成一对
  **测试密钥**写入公钥槽并重新编译，再按本机真实机器码签发三档真码供测试使用。
  **发布前务必把公钥槽换回生产公钥**（占位或测试公钥发出去等于没有授权）。

## 3. 有效期与三档

| 档位 | 标识 | 天数 | 说明 |
| --- | --- | --- | --- |
| 一个月 | `M` | 30 天 | 短期试用/单项目 |
| 半年 | `H` | 183 天 | 常规采购 |
| 一年 | `Y` | 365 天 | 年费 |

- 天数按自然日算，显示到期日按本机时区折算。
- 剩余 14 天以内开始提醒，到期当天授权立即失效并弹激活窗口。
- 续期就是再输一个新码：新码的档位与到期日直接覆盖旧的，不需要先卸载。
- 有效期以**签发日**为起点，不是以激活日为起点。所以"发给客户的码"最好当天签发；
  `new` 命令支持第三个可选参数指定签发日（`YYYY-MM-DD`），补发历史授权时用得上。

## 4. 反破解设计（当前实现）

目标是"改代码绕过授权"的成本远高于买授权，而不是理论上不可破。已实现的层次：

1. **离线非对称签名**：ECDSA-P256/SHA-256，管理员用私钥签发，插件里只有公钥。
   没有私钥无法伪造任何激活码，改代码也不能"算出"一个有效码。
2. **机器绑定**：激活码里带机器指纹（主板 GUID + 系统卷序列号 + BIOS UUID 的 SHA-256 派生，
   取 5 字节短指纹），验签通过后还要比对本机指纹；激活文件本身用 DPAPI 机器范围密钥加密，
   拷到别的机器无法解密。
3. **防时间回调**：把"见过的最大时间"持久化（HKCU），系统时间往回拨超过 7 天即暂停授权；
   7 天容差用于容忍时区、NTP 校正和主板电池更换。
4. **防状态重置**：删掉激活文件但计数器还在，会被识别为"状态被重置"并回到未激活。
5. **公钥与常量掩码**：公钥坐标在源码里是 Base64 + 掩码 + 派生种子的形式，直接看源码得不到
   公钥；把掩码换掉会导致验签与自检同时失败。
6. **运行期环境自检**：调试器、profiler 环境变量、常见反编译/调试器模块（dnspy、IlSpy、
   de4dot、x64dbg 等）都会被计入环境状态位。
7. **构建期版本戳**：`tools/build.ps1` 同时写入程序集版本与 `LicenseBuild.cs` 里的常量，
   运行期比对两者；把授权模块删掉换一套实现，这个等式就不再成立。
8. **栅栏分散**：命令入口（`AddIn.Run`）、工具窗口入口（`ToolContext.Show`）、
   模型写入路径（`CadBuilder.CreatePart`、`AutoHoleCad.Drill`、`CadConvertSession`）
   各自独立校验，任何一处被绕过不影响其余处。
9. **失败同形**：深层栅栏失败时抛的是"模型操作未能完成，请重试或检查当前选择"，
   与普通 CAD 操作错误完全一样，不给破解者留下"这里就是授权点"的路标。

### 还没做、按需可加的手段

- IL 级混淆（ConfuserEx / .NET Reactor）或把关键校验搬进原生 DLL：需要额外工具链与签名流程，
  当前环境没有可用的混淆器，故未强行引入。
- 联网心跳/在线吊销：需要服务端，客户多为内网离线环境，暂无计划。
- 代码签名证书：能提升安装体验与防篡改观感，但需要购买证书。

## 5. 验证

```powershell
# 只跑授权链（71 项断言：格式往返、抄写校验、篡改检测、三档天数、到期、机器绑定、
# 续期、时间回调容差、状态重置、以及"管理员工具签发的真码能被插件接受"）
DEV_0.7_授权\tools\LicenseAdmin\make-test-codes.ps1
DEV_0.7_授权\build\license-test\PanelTests.exe --license

# 完整回归（授权 + 几何 + 自动打孔等，85 项核心断言）
DEV_0.7_授权\build\license-test\PanelTests.exe
```

`make-test-codes.ps1` 做三件事：生成一对**测试密钥**并把公钥写进插件公钥槽（公钥槽不对齐时会
重新编译）、用管理员工具按本机真实机器码签发三档真码、把码写到 `build/license-test/admin-codes`。
测试私钥是 `tests/fixtures/license-test.tgkey`（已 gitignore），**只用于本机测试**，与生产私钥是两回事；
生产环境请按第 2 节重新 keygen 并覆盖公钥槽。

如果测试私钥缺失、或与插件内嵌公钥不成对，验签相关用例会打印 `SKIP` 后跳过，其余测试照常进行，
退出码仍为 0。

## 6. 代码结构

| 文件 | 作用 |
| --- | --- |
| `src/License/LicensePayload.cs` | 27 字节签名载荷格式（掩码/档位/指纹/签发日/随机数） |
| `src/License/LicensePlans.cs` | 三档定义（30/183/365 天，编号 11/27/42） |
| `src/License/LicenseCodec.cs` | 激活码编解码（32 字符表、每字符 5 bit、抄写校验） |
| `src/License/LicenseSignature.cs` | ECDSA-P256 验签与签发 |
| `src/License/LicenseKeySlot.cs` | 公钥槽（由 keygen 覆盖；默认占位） |
| `src/License/LicenseKeyMaterial.cs` | 公钥去掩码与 CNG BLOB 组装 |
| `src/License/LicenseMachine.cs` | 机器指纹与机器码 |
| `src/License/LicenseStore.cs` | 激活文件落盘、DPAPI、单调时间基准 |
| `src/License/LicenseLibrary.cs` | 授权链主流程（校验顺序、激活、令牌、闸门） |
| `src/License/LicenseGuard.cs` | 环境自检与完整性自检 |
| `src/License/LicenseGate.cs` | 业务栅栏（交互式与静默两种） |
| `src/License/LicenseUi.cs` | 激活窗口 |
| `src/Admin/LicenseAdminKey.cs` | 管理员侧密钥读写与签发（**不进插件 DLL**） |
| `src/Admin/LicenseAdminMain.cs` | 管理员命令行入口（**不进插件 DLL**） |

## 7. 激活码格式（实现说明）

```
载荷 27 字节：掩码(1) + 'T''G'(2) + 版本(1) + 档位(1) + 机器指纹(10) + 签发日(4,大端) + 随机数(8)
签名 64 字节：ECDSA-P256 / SHA-256（IEEE P1363）
正文 91 字节：每 5 字节 → 8 个字符（每字符 5 bit），19 组
校验 1 组 8 字符：正文 HMAC-SHA256 的前 5 字节
合计 20 组 = 160 字符，显示时按 5 字符分段，共 191 个可见字符（含 31 个 '-'）
```

字符表 `0123456789abcdefghjkmnpqrstvwxyz`（32 个，去掉 i l o），大小写不敏感。

随机数保证"同一天、同一机器、同一档位"重复签发也是不同的码；签发日精确到天，
所以同一天补发不会产生相同签名。
