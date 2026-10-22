# 0.7.11 交接（2026-10-21 晚）：带链接的零件打不上孔

用户原话：「请继续 0.7.11 的插件制作。插件有一些 BUG，可能在存在链接的零件内无法进行打孔，需要断开链接才能打孔，这个可能是天工的 BUG，也可能是插件的 BUG，请你找出根因并修复无法打孔的问题。」「做好了请帮我安装并激活。」

## 一、查到了什么（全部在用户装配的**副本**上做，原文件没动）

副本：`H:\桌面\YF25.07\焊接台车` 整个复制到会话 scratchpad（1.07 GB）。探针都在 `tools/hole-feature-probe/`，结果在 `artifacts/<tag>/out.txt`。

| 探针 | 结论 |
| --- | --- |
| `LinkCensus.cs`（`link-census`） | YF25.07G01-L1-M30-02.asm 里 **11 个零件 HasInterpartLinks=True**，链接分别属于 **5 个不同的装配**（`GetInContextAssemblyNameForInterpartLinks`）：-2.par 属于 YF25.07G01-L1-M30-02.asm；-22/-27/-19/-26 属于 YF25.07-L1-M30-03.asm；-14/-28 属于 YF25.07-L1-M50-06-01.asm…… 大多数**没有零件间复制**（原位设计时包含/投影了别的零件的边，天工也算链接） |
| `LinkedPartProbe.cs`（`linked-part-probe`、`lp-27`、`lp-22`） | 直接打一个通孔：装配上下文 / 原位 / 冻结链接后 / 解冻后，**全部打得上** |
| `LinkedDrillE2E.cs`（`linked-drill-*`、`fix-top-multi`） | 走「自动打孔」窗口真实路径（点参考孔 → 点面 → 打孔，1 个和 20 个）。**在不是建链的装配里保持关联：`InterpartConstructions.Add` 一律 E_FAIL**（= 用户日志 13:41、15:18、15:19 那些 `NativeCopy … E_FAIL`）；在建链的那个装配里就建得出来（20 孔 16 个原生关联） |
| `LinkStaleProbe.cs` / `LinkInactiveProbe.cs` / `LinkSourceEditProbe.cs` | 想造出"链接过期"：挪来源零件（有装配关系，Move E_FAIL 挪不动）、来源零件设成未激活、先在来源面上打孔再打目标 —— **都打得上**，没复现出 0x80010108 |

**确定的根因（天工的规则 + 插件没处理）**：天工只允许一个零件的零件间链接属于**一个**装配。用户在 YF25.07底部…小车.asm（下一级）里给 -2.par 打孔并勾「保持关联」，
而 -2.par 的链接属于上一级 YF25.07G01-L1-M30-02.asm → 建零件间复制 E_FAIL。插件原来：认实例（大装配里遍历，现场「关联准备 166 秒」）→ 进原位 → 建复制失败 → **退出原位 → 在装配上下文里打孔**。

**现场打不上孔的那几批（0.7.9 13:41 / 15:22 / 16:32，0.7.10 13:30）全部是在装配上下文里直接改零件文档打的**：每个孔一侧返回失败状态、另一侧 0x80010108「系统无法找到指定的资源」，3 轮全败。
天工自己的孔命令只能在原位编辑里用（双击零件）；用户断开链接以后就打得上。
副本上新开的 CAD 里装配上下文也打得上 —— **0x80010108 这一半没能在副本上复现**，触发条件应该是用户那个开了很久的会话里的状态（多次原位进出、改过来源零件……），没找到办法离线造出来。
所以修法是：**带链接的零件一律走天工自己能用的那条路（原位编辑里打）**，并且任何一次"对象已断开"都自动换个环境补打。

## 二、改了什么

| 改动 | 文件 |
| --- | --- |
| 原生关联开始前先问「这个零件的链接属于哪个装配」：属于别的装配 → **不认实例、不进原位、不建复制**，直接退回插件关联，结果里说清楚「链接是在 X.asm 里建的，要原生关联请打开 X.asm 再打孔」 | `HoleNativeLink.cs` `Session.Begin` ⓪、`LinksBelongElsewhere`；路径比较 `NativeLinkMath.SamePath`（纯逻辑，有单测） |
| 原生关联一个孔都没挂上时**不退出原位**，孔就在原位里打（原来退回装配上下文打 = 现场失败的那条路） | `HoleNativeLink.cs` `Begin` 末尾 |
| **带零件间链接的零件（HasInterpartLinks）一律原位编辑里打**（不管勾没勾保持关联） | `AutoHoleForm.RunDrill`、`NativeHoleLink.EnterTarget`（从 Begin 里拆出来）/ `HasLinks` |
| 打完还有孔因为 0x80010108 / CO_E_OBJNOTREG / CO_E_OBJNOTCONNECTED 没打上：**换个编辑环境（装配 ↔ 原位）把没打上的再打一遍**，结果合并（失败以补打为准），日志 `AutoHoleRetryContext` | `AutoHoleWriter.DrillRequests` 记 `HoleRequest.Done/Disconnected`、`DrillResult.Disconnected`；`MergeRetry`；`AutoHoleForm.RunDrill` |
| `install.ps1`：注册时删掉 InprocServer32 下**别的版本号子键**（CLR 加载版本号最大的那个，降级装回旧版会白装 —— 0.7.10 → 0.7.9 那次实锤） | `tools/install.ps1` |
| 点到"没有对象"时提示可能是零件没激活（重开大装配后常见），先右键「激活」 | `AutoHoleForm` / `AutoHolePatternForm` / `HoleEditForm` 的 `MouseClick` |
| VERSION = 0.7.11 | |

## 三、验证

* `tests/pure` 全过（新增 SamePath 3 条）；`PanelTests` 核心 106、`--autohole-ui` 87 全过，跑前跑后 activation.dat 哈希一样。
* 真机（私有桌面 CAD，用户装配副本）`fix-top-multi`：YF25.07底部…小车.asm 里给 -2.par 一次打 20 个孔 —— 保持关联：20/20，没有再去建复制（没有 E_FAIL），说明里给出该去哪个装配；不保持关联：20/20；撤销都干净。
* 回归：见下面「四」（DrillSpeedE2E link/nolink/mixed/bars4、NativeLinkE2E、LinkSyncE2E、EditPatternE2E）。

## 四、回归结果

新 DLL（开发构建 %TEMP%\tg711），私有桌面 CAD，`artifacts/reg-*`：

| 用例 | 结果 |
| --- | --- |
| `DrillSpeedE2E` link / nolink / mixed / bars4 | 前三个全过（link 3.5 s、nolink 0.86 s）；bars4 9.86 s 超 5 s —— **和 0.7.10 一样**（9.71 s，进出原位的大头，老问题） |
| `NativeLinkE2E` | OK（37 条） |
| `EditPatternE2E` | OK |
| `LinkSyncE2E` | 17 条 FAIL —— **拿 0.7.10 的开发构建跑一模一样的 17 条**（`reg-link-sync-0710`），不是这次改出来的：用例还按 0.7.3 的期望数单孔特征 / 属性集关联记录，0.7.9 起孔都是原生关联 + 多孔孔特征。孔跟着挪、切除体积这些实际行为都过。要改用例 |

安装：`tools\build-install.ps1` → `build\release-20261021-212645`（正式构建）；install.ps1 删掉了 0.7.2.0～0.7.9.0 七个旧版本子键，只剩 0.7.11.0。
私有桌面起 CAD 核对：`AddInConnect … release-20261021-212645 … ver=0.7.11.0`，「天工工具箱 0.7.11 Connect=True」。
激活：本机一年码 yvz7-259y 仍有效（`LicenseLibrary.Current()` = Valid，到期 2027-10-20），换版本不用重新激活。

## 五、没做完 / 下次注意

* **0x80010108 在副本上没复现**。如果用户装了 0.7.11 以后某个零件仍打不上：看 panel.log 里有没有 `AutoHoleRetryContext`（说明两种环境都试过了）和 `AutoHoleLinkedEnter`（进不了原位的原因），以及失败零件的 `AutoHoleTiming` 行；再问用户当时开的是哪个装配、零件是不是刚改过来源。
* 带链接的零件每张面多一次进出原位（实测 -2.par 进 ~2 s、退 ~1.5 s）。
* 0.7.10 交接里列的回归（FenceHoleE2E、CheckPatternE2E、NewFeaturesE2E）这次没跑。
* 用户原文件 13:32 存过一次 0.7.10 打孔失败后的状态：副本里 -2.par 的特征树没有失败的孔特征（28 项是 Warned/Suppressed 的用户自己的特征），零件间复制 10 个全部 OK。
