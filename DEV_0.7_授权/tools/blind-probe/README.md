# tools/blind-probe —— 打孔基准面 / 盲孔诊断探针

2026-10-05 排查"装配里打盲孔老报错、打贯通孔就可以"时写的隔离实验程序。
根因与结论见 `DEV_0.7_授权/盲孔报错-根因与修复-20261005.md`。

这些探针**连到正在运行的天工 CAD**（`Marshal.GetActiveObject("SolidEdge.Application")`），
走的就是生产代码路径（`AutoHoleWriter.DrillRequests` / `FindOrCreatePlane` 反射调用），
用来验证"打孔基准面到底建在哪儿、朝向对不对、盲孔/贯通孔各自成不成"。

## 铁律：只在副本上跑

1. 先把目标装配**整个文件夹**复制到工作目录（`Copy-Item -Recurse`），对副本跑探针。
   `BlindProbe` 会真打孔 —— 绝不能直接对用户的装配跑。
2. 跑完用 `Cleanup.exe <副本目录>` 把探针打开的副本文档全部关掉（不保存），
   只留用户自己的文档。`Cleanup.exe <目录> --list` 可以先看清单。
3. 不要在用户 CAD 正在干活时切活动文档；探针里已经尽量少 Activate。

## 编译（x64，.NET Framework csc，和插件同一套引用）

```powershell
$cad='C:\Program Files\NDS\TianGong 2025'
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$interop=Join-Path $cad 'Program\TGAiHelper\Interop.TG.dll'
& $csc /nologo /target:exe /platform:x64 /out:BlindProbe.exe /reference:$interop /reference:TianGongCadSuite.dll BlindProbe.cs
```

TianGongCadSuite.dll 用 `tools\build.ps1 -OutputDirectory <目录>` 现编一份，和 Interop.TG.dll 放同目录。

## 四个探针

| 探针 | 用法 | 干什么 |
|---|---|---|
| `BlindProbe.exe` | `BlindProbe.exe <装配副本.asm> <零件名片段> [深度mm]` | 选该零件"朝上最大平面"→ 打印生产逻辑选中的基准面（根点/法向/离面距离）→ 同一孔心先打盲孔、再打贯通孔、再换孔心打一次盲孔 |
| `SeqProbe.exe` | `SeqProbe.exe <装配副本.asm> <零件名片段> <through\|blind-top\|blind-bottom\|preplane-blind>` | 每次只做一件事（每个模式都应该用**全新副本**），用来分离"位置/朝向/上下文"哪个变量在起作用；`fixture` 模式不需要装配，自己新建 100×100×20 板打盲孔 |
| `SideProbe.exe` | `SideProbe.exe <装配副本.asm> <零件名片段> <sides\|preplane>` | `sides`：从平行基准面用两个方向各建一张，打印根点法向（看清 `igNormalSide` 约定）；`preplane`：先用面片建共面基准面再走生产逻辑 |
| `Cleanup.exe` | `Cleanup.exe <目录> [--list]` | 关闭探针打开的副本文档（不保存），收尾用 |

## 已验证过的判据（2026-10-05）

- 打孔基准面**必须落在所选面那张平面上**：偏 40mm 时盲孔失败、贯通孔成功。
- 打孔基准面**法向必须背对材料**：位置精确但法向扎进材料时，两个方向都建不出孔。
- 外法向 = `Face.IsParamReversed ? -几何法向 : 几何法向`。
- `AddParallelByDistance(面片, 0, igNormalSide, …)` 的位置偏差 0mm、法向朝外；
  `FlipNormal` 参数对"平行面 + 偏移"建法无效。
