# 0.7.12 交接（2026-10-22）：链接打孔可用、不再卡住

用户原话：「打孔总是卡住……有链接打孔总是失败，然后回退老逻辑分组放单个孔，我不要这个老逻辑，我要一个特征里带有很多链接孔的那种。
还有打孔可能都打完了，但是天工CAD窗口还是卡住，没有刷新，插件也卡住，我不要再有这种问题出现了。现在唯一的指标是让链接打孔可用，然后耗时先不要管。」

全部在用户装配 `H:\桌面\YF25.07\焊接台车` 的**副本**上做（scratchpad，原文件没动）。探针 / E2E 在 `tools/hole-feature-probe/`，结果在 `artifacts/c712-*`、`artifacts/g-*`。

## 一、现场日志（0.7.11，2026-10-22 上午）

* 08:40 `-15.par` 4 孔：进原位 **85 秒**，`InterpartConstructions.Add` ×4 **E_UNEXPECTED**，合成 0 个（逐个单孔），退出原位 **31 秒**（= 两次 15 秒超时）；
* 09:33 `-25.par` 4 孔：关联准备 **106 秒**（属性集关联给整个大装配建实例索引），合成 0 个。

## 二、真机查明的事实（都是新的）

| 事实 | 探针 |
| --- | --- |
| 在装配 X 里原位编辑，**实例链经过的子装配 T 自己也开着窗口**：天工把原位编辑"借"到 T 的窗口（`GetTopDocumentAndSubOccurrenceOfIPADoc` 答 T 不答 X），最后一级关闭并返回等 15 秒也回不来；**之后这个零件上打孔一律 CO_E_OBJNOTCONNECTED / CO_E_OBJNOTREG、轮廓过不了闭合检查**，关掉 X 也恢复不了。= 现场 08:40 的样子 | `OwnerCtxE2E`（已删，结果 `artifacts/c712-ownerctx3`） |
| 零件自己开着窗口时在装配里原位编辑它：退出后活动窗口变成**零件的窗口**（装配看上去不刷新） | `WindowCloseProbe` R5 |
| **每进一次原位编辑，天工给实例链上的子装配和零件各开一个看得见的窗口，退出后还在**（`app.Windows` 里，`app.Documents` 里没有） | `WindowStateProbe`、`InPlaceDirectProbe` |
| **关掉这种窗口 = 天工重新加载那个文档：原位编辑里做的改动没了**（加的基准面 4→3），插件手里的对象全部 0x80010108 / 0x800706B5 | `WindowCloseProbe` R1/R2 |
| 把这种窗口 `Visible=false` 藏起来：改动都在、下一次原位编辑照常（复用这些窗口，不再新开）、活动窗口还是装配 | `WindowHideProbe` |
| 选 `SubOccurrence.Reference` + 57872 能**一步**进到子装配里的零件原位编辑（原来以为只能一级一级进）；照样留窗口 | `InPlaceDirectProbe` M3（没用上，记一笔） |
| 新开的 CAD 里，在建链装配本身（MC模组侧向支撑.asm / -15、M30-02.asm / -25）打孔 + 原生关联都正常 | `LinkedDrillE2E` `c712-mc-15`、`c712-m3002-25` |
| 「万向轮范围--兼容小车.par」（CMGS150-U.stp.asm 里，STEP 导入的范围件）怎么都打不上孔（轮廓闭合检查失败），和关联无关 | `c712-wheel` |

## 三、改了什么

| 改动 | 文件 |
| --- | --- |
| 「保持关联」**只用天工原生关联**，不再退回属性集关联（那条路只能单孔 + 编组，还要给大装配建实例索引 100 秒）。挂不上关联的孔照样进**同一个多孔特征**，结果里说原因 | `AutoHoleForm.RunDrill`、`AutoHoleCad.DrillRequests` |
| 多孔特征里某个孔挂不上关联：这个孔不挂、整批照样合成（原来整批散成单孔） | `HoleCombine.SharedProfiles` |
| **进原位之前查实例链上的文档是不是在别的窗口里开着**（`app.Documents`）：是就这一面不打，结果里说"先关掉「T」的窗口"——不再把天工搅乱 | `NativeHoleLink.OpenElsewhere` / `TargetOpenElsewhere`，`Enter`，`RunDrill` |
| 进原位以后问天工"是不是真的在这个装配里原位编辑"（`InPlaceActivated` + `GetTopDocumentAndSubOccurrenceOfIPADoc`），不是就退出、报错，不在错误状态里建复制 | `NativeHoleLink.EnterTarget` / `InPlaceUnder` |
| **原位编辑留下的窗口藏起来**（只藏文档不在 `app.Documents` 里的，用户自己开的不碰） | `NativeHoleLink.HideLeftovers`（`Leave`、`BackTo` 里调） |
| 打孔期间**不再关屏幕刷新**；一批打完（含出错）确认活动文档回到开始时的那个（还在原位就逐级关闭并返回，在别的窗口就切回来），再刷新视图 | `AutoHoleForm.RunDrill`、`NativeHoleLink.BackTo` / `Refresh` |
| 开始打孔前查参考孔的零件 / 面对象还活着没有（文档被重新加载过就让用户重选，不再每个孔失败一次） | `AutoHoleForm.DeadReference` |
| 链接属于别的装配：说明改成"孔已经打好（没关联）；要关联请打开「X」在那里打孔"（试过自动切到 X 去建关联 —— X 一般是上一级，实例链必经过当前装配自己，正是上面第一条，放弃） | `NativeHoleLink.Session.Begin` ⓪ |
| 进/退原位每一级、建复制失败时的上下文（零件的建链装配、顶层、活动文档）都写进 panel.log（`NativeEnter` / `NativeLeave` / `NativeCopy` / `NativeNote` / `NativeHide` / `NativeBackTo` / `AutoHoleBusyDoc`） | `HoleNativeLink.cs` |
| 一批里两个孔互相重叠（腰孔两头被当成两个参考孔）：重叠的那个不打、说明原因，其余照样合成（原来整批合不成、散成单孔且全部失败） | `HoleSpots.Overlaps`，`AutoHoleCad.DrillRequests` |
| VERSION = 0.7.12 | |

## 四、验证

* 离线：`tests/pure` 全过（新增 HoleSpots 重叠 5 条）；`PanelTests` 核心 106、`--autohole-ui` 87 全过，跑前跑后 activation.dat 哈希一样。
* 安装：`toolsuild-install.ps1` → `buildelease-20261022-133230`（正式构建），删掉 0.7.11.0 子键；私有桌面起 CAD：
  `AddInConnect … release-20261022-133230 … ver=0.7.12.0`。激活没动（一年码仍有效，换小版本不用重新激活）。

## 五、回归结果（新 DLL，私有桌面 CAD，用户装配副本 / 合成装配）

| 用例 | 结果 |
| --- | --- |
| `LinkedDrillE2E` MC模组侧向支撑.asm，自动挑零件（`*`）→ -17.par 一张面 **20 个孔、保持关联** | **20/20 挂上天工原生关联**，4 个规格各一个多孔特征（Φ6 ×7、Φ8 ×2…），回到顶层、看得见的窗口不变；不关联那次也是多孔特征（`artifacts/h-auto-mc`） |
| `DrillSpeedE2E` link / mixed / bars4（合成装配） | 20 个孔一个（或每规格一个）多孔特征、全部原生关联、挪参考件后孔跟过去、撤销干净；只有"5 秒内"三条 FAIL（7.3 s / 7.7 s / 15.6 s —— 去掉了关屏幕刷新，用户说耗时先不管）（`artifacts/g-drill-speed`） |
| `LinkedDrillE2E` 小车.asm / -2.par（链接属于 M30-02.asm）20 孔 | 两次都 20/20、两个多孔特征；说明"链接属于 M30-02.asm，到那里打才能关联"；留下的窗口藏起来、第二次原位复用（`artifacts/g-xiaoche-2`） |
| `LinkedDrillE2E` MC / -15、M30-02 / -25 | 原生关联、回到顶层、窗口不变多（`artifacts/g-mc-15`、`g-m3002-25`） |
| `BusyDocE2E` X=M30-02.asm 与 T=小车.asm 同时开着，在 X 里给 T 里的零件打孔 | **拒绝**、一个孔都不动、不建复制、活动文档还是 X、说清楚关哪个窗口（`artifacts/g-busydoc`、`h-busydoc`）。③④ 在 T 里打的 FAIL 是挑的测试位置不对（-4.par 的面打不下 Φ20；E-JBEM30 那两个位置本来就有 Φ24 孔，插件正确报"已经有孔"） |
| 「万向轮范围--兼容小车.par」 | 任何方式都打不上（STEP 导入的范围件，和这次改动无关；天工对这种几何失败也报 CO_E_OBJNOTCONNECTED） |

## 六、没做 / 下次注意

* 链接属于上一级装配（典型：零件的链接是在 M30-02.asm 里建的，用户在 小车.asm 里打孔）：天工规则所限，只能到 M30-02.asm 里打孔才挂得上关联。插件现在照样把孔打好（多孔特征，不挂关联）并说明。
* 用户现场如果还开着 0.7.11 以前留下的子文档窗口：0.7.12 打完一批会顺手藏起来；**如果用户已经关过那种窗口，那个零件里没保存的改动已经没了**（天工重新加载）——这条要跟用户说。
* `InPlaceDirectProbe` 发现的一步进原位（`SubOccurrence.Reference`）没用上；现在一级一级进的路径真机验证得多，没动。
