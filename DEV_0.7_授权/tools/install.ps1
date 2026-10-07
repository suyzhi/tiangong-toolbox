param(
    [switch]$Uninstall,
    [string]$LibraryPath,
    # 安装包用：先把 payload 复制到这个目录，再注册那份副本（装完包可以删/挪走，插件不受影响）。
    [string]$StageDirectory,
    # 无人值守：不弹窗，直接结束正在运行的天工进程。
    [switch]$Force
)
$ErrorActionPreference='Stop'
if(![Environment]::Is64BitProcess){throw 'Please run 64-bit Windows PowerShell.'}
$NL=[Environment]::NewLine
$guid='{8C05165C-65A4-4EF2-A138-508589D82004}'
$prog='TianGongCadSuite.SuiteDevAddIn'
$classPath='Software\Classes\CLSID\'+$guid
$reg=[Microsoft.Win32.Registry]::CurrentUser

# 需要连带复制的同目录文件（插件运行时要用）：互操作库、格式转换器、Lineup 默认型号表。
$companions=@('TianGongCadSuite.dll','Interop.TG.dll','TianGongConverter.exe','lineup-models-seed.xml','lineup-sample.tsv')

function Get-TianGongProcesses {
    # 只认"天工 CAD 本体 + 本插件拉起的工具进程"，绝不碰别的程序。
    $names=@('TianGong','TianGongConverter','TianGongDrillWorker','PanelLauncher','TrainingExportRunner')
    @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.ProcessName })
}
function Stop-TianGongProcesses($list){
    foreach($p in $list){
        try{ Write-Output ('  正在结束 ' + $p.ProcessName + ' (PID ' + $p.Id + ')'); Stop-Process -Id $p.Id -Force -ErrorAction Stop }
        catch{ Write-Output ('  结束失败 PID ' + $p.Id + '：' + $_.Exception.Message) }
    }
}
# 安装/卸载前统一处理：检测 -> 询问 -> 强制结束 -> 确认已经干净。
# 为什么必须做：天工 CAD 是单实例程序，旧进程不退会导致"注册表换了 DLL、CAD 却还加载旧版"，
# 用户就得自己一个个关、甚至重启电脑。这里明确问一句"是否强制关闭"，答是就代为结束。
function Get-RunningText($procs){
    @($procs | ForEach-Object { '    ' + $_.ProcessName + '  (PID ' + $_.Id + ')' }) -join $NL
}
function Confirm-CloseRunning {
    $procs=Get-TianGongProcesses
    if($procs.Count -eq 0){ return }
    $list=Get-RunningText $procs
    Write-Output ('检测到 ' + $procs.Count + ' 个天工进程正在运行：')
    Write-Output $list
    if(-not $Force){
        $lines=@(
            ('检测到 ' + $procs.Count + ' 个天工进程仍在运行：'),
            '',
            $list,
            '',
            '天工 CAD 是单实例程序，旧进程不退出会导致：',
            '   - 新版本加载不上（CAD 还在用旧的 DLL）；',
            '   - 注册表已指向新 DLL，功能区里却还是旧插件。',
            '',
            '是否强制结束这些进程并继续？',
            '（注意：未保存的模型 / 图纸修改会丢失。）',
            '',
            '是 = 强制结束并继续      否 = 退出，你先自己保存关闭'
        )
        $text=($lines -join $NL)
        $answer=$null
        try{
            Add-Type -AssemblyName System.Windows.Forms | Out-Null
            $answer=[System.Windows.Forms.MessageBox]::Show($text,'天工工具箱 安装程序',[System.Windows.Forms.MessageBoxButtons]::YesNo,[System.Windows.Forms.MessageBoxIcon]::Warning)
        }catch{
            try{ $reply=Read-Host '检测到天工进程正在运行，是否强制结束并继续？(Y/N)'; if($reply -match '^(y|Y|yes|YES|是)$'){ $answer='Yes' } }catch{}
        }
        if("$answer" -notmatch '^(Yes|6)$'){
            Write-Output '已取消。请先保存并关闭天工 CAD，然后重新运行安装/卸载。'
            exit 2
        }
    }
    Stop-TianGongProcesses $procs
    for($i=0;$i -lt 20;$i++){ Start-Sleep -Milliseconds 1000; if((Get-TianGongProcesses).Count -eq 0){ break } }
    $left=Get-TianGongProcesses
    if($left.Count -gt 0){ Stop-TianGongProcesses $left; Start-Sleep -Seconds 3 }
    $left=Get-TianGongProcesses
    if($left.Count -gt 0){ throw ('仍有天工进程没能结束（PID ' + (($left | ForEach-Object { $_.Id }) -join ',') + '）。请手动结束后重试。') }
    Write-Output '天工进程已全部结束，继续。'
}

if($Uninstall){
    Confirm-CloseRunning
    $reg.DeleteSubKeyTree($classPath,$false)
    $reg.DeleteSubKeyTree('Software\Classes\'+$prog,$false)
    Write-Output '已卸载天工工具箱（当前用户）。重启天工 CAD 后功能区不再出现该插件。'
    exit
}

$root=Split-Path $PSScriptRoot -Parent
$dll=if($LibraryPath){[IO.Path]::GetFullPath($LibraryPath)}else{Join-Path $root 'build\TianGongCadSuite.dll'}
if(!(Test-Path $dll)){throw '找不到插件 DLL。请用 -LibraryPath 指定 payload\TianGongCadSuite.dll。'}

# 顺序很重要：**先清进程，再复制**。
# 旧版本是把复制放在前面，结果"CAD 正开着时升级"会撞上"文件被占用"（实测复现：
# app\TianGongCadSuite.dll 正在被运行中的 CAD 用着，Copy-Item 直接 IOException）。
Confirm-CloseRunning

if($StageDirectory){
    $stage=[IO.Path]::GetFullPath($StageDirectory)
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $from=Split-Path -Parent $dll
    foreach($f in $companions){
        $s=Join-Path $from $f
        if(-not (Test-Path $s)){ continue }
        # 进程虽然退了，文件句柄偶尔还要几百毫秒才释放；这里重试几次再报错。
        $copied=$false
        for($try=1;$try -le 8 -and -not $copied;$try++){
            try{ Copy-Item $s $stage -Force; $copied=$true }
            catch{ if($try -eq 8){ throw ('复制 ' + $f + ' 失败：' + $_.Exception.Message + '（该文件仍被占用，请确认天工 CAD / 转换器已经完全退出）') }; Start-Sleep -Milliseconds 600 }
        }
    }
    $dll=Join-Path $stage 'TianGongCadSuite.dll'
    Write-Output ('已把插件文件安装到：' + $stage)
}

$assembly=[Reflection.AssemblyName]::GetAssemblyName($dll)
$key=$reg.CreateSubKey($classPath)
$key.SetValue('','TianGongCadSuite.SuiteDevAddIn')
$key.SetValue('409','TianGong Toolbox (DEV 0.7.2)')
$key.SetValue('804',[string]([char]0x5929)+[char]0x5DE5+[char]0x5DE5+[char]0x5177+[char]0x7BB1+' (DEV 0.7.2)')
$key.SetValue('AutoConnect',1,[Microsoft.Win32.RegistryValueKind]::DWord)
$key.CreateSubKey('Implemented Categories\{26B1D2D1-2B03-11D2-B589-080036E8B802}').Dispose()
$key.CreateSubKey('Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}').Dispose()
$key.CreateSubKey('Environment Categories\{26618395-09D6-11D1-BA07-080036230602}').Dispose()
foreach($category in @('{26618396-09D6-11D1-BA07-080036230602}','{26618398-09D6-11D1-BA07-080036230602}','{08244193-B78D-11D2-9216-00C04F79BE98}','{D9B0BB85-3A6C-4086-A0BB-88A1AAD57A58}','{9CBF2809-FF80-4DBC-98F2-B82DABF3530F}')){$key.CreateSubKey('Environment Categories\'+$category).Dispose()}
$server=$key.CreateSubKey('InprocServer32')
$server.SetValue('','mscoree.dll');$server.SetValue('ThreadingModel','Both')
foreach($target in @($server,$server.CreateSubKey($assembly.Version.ToString()))){
    $target.SetValue('Assembly',$assembly.FullName);$target.SetValue('Class','TianGongCadSuite.PanelAddIn')
    $target.SetValue('RuntimeVersion','v4.0.30319');$target.SetValue('CodeBase','file:///'+$dll.Replace('\','/'))
}
$key.CreateSubKey('ProgId').SetValue('',$prog)
$reg.CreateSubKey('Software\Classes\'+$prog+'\CLSID').SetValue('',$guid)
$key.Dispose();$server.Dispose()

# —— 加载项记录（当前用户）自检与修复 —— 2026-09-29 现场事故换来的，别删 ——
# CAD 认不认这个插件，**不只看 COM 注册**；它还要看当前用户下这条记录里的 AutoConnect：
#     HKCU\Software\NDS\TianGong\Version 225\AddIns\{GUID}
# 为 0 时 CAD 照样把它列进会话日志的 "Registered AddIns"（GUID、路径、版本全对），
# 但紧接着那行写的是 **Connect: FALSE** —— 插件永远不连接，功能区里什么都不出现，
# 而安装程序这边**没有任何报错**，用户看到的就是"提示安装成功、软件里却没有"。
# 事故经过：这条记录被清过一次（CAD 会把它重建为 AutoConnect=0），此后每一次安装都
# "注册完成"、插件一次都没连上过。所以安装时必须把它拉回 1，并且回读确认。
# 只改这一个值：描述名、Cookie 原样保留；**绝不删除整条记录**（删了 CAD 只重建、不加载）。
$ndsRoot='Software\NDS\TianGong\Version 225\AddIns'
$ndsPath=$ndsRoot+'\'+$guid
$display='天工工具箱 (DEV 0.7.2)'
$nds=$reg.OpenSubKey($ndsPath,$true)
if($nds -eq $null){
    # 本机还没有这条记录（从没成功加载过）：按 CAD 自己的格式预置一份。
    # Cookie = GUID 前 8 位十六进制当 DWORD —— 本机现存 14 条记录逐条核对过，全是这个规律。
    $u=[Convert]::ToUInt32(($guid -replace '[{}]','').Substring(0,8),16)
    $cookie=if($u -gt [int]::MaxValue){[int]($u-4294967296)}else{[int]$u}
    $nds=$reg.CreateSubKey($ndsPath)
    $nds.SetValue('',$display)
    $nds.SetValue('Cookie',$cookie,[Microsoft.Win32.RegistryValueKind]::DWord)
    $nds.SetValue('AutoConnect',1,[Microsoft.Win32.RegistryValueKind]::DWord)
    Write-Output ('已预置加载项记录（本机原先没有）：' + $ndsPath)
}else{
    $was=$nds.GetValue('AutoConnect')
    if("$was" -ne '1'){
        $nds.SetValue('AutoConnect',1,[Microsoft.Win32.RegistryValueKind]::DWord)
        Write-Output ('已修复加载项记录：AutoConnect ' + $(if($was -eq $null){'(缺失)'}else{$was}) + ' -> 1')
        Write-Output ('    ' + $ndsPath)
    }
}
if($nds -ne $null){
    $now=$nds.GetValue('AutoConnect')
    $nds.Dispose()
    if("$now" -ne '1'){ throw ('加载项记录没能改成 1（当前值 ' + $now + '），CAD 不会加载本插件：' + $ndsPath) }
}

# 换版本时的坑（实测）：只改 COM 注册、直接重启 CAD，CAD 有时仍加载**上一次**那份 DLL
# （踩过：注册表已指向 0.6 的 DLL，失败栈里的源码路径却还是 0.7 的），于是"换版本 A/B"会得出错误结论。
# 本脚本已经先结束天工进程再注册，规避了这一点；下面这条自证命令是为了核对。
#
# 注意：**不要**去删 HKCU\Software\NDS\TianGong\*AddIns\{GUID} 那个键（试过）——
# 删掉之后 CAD 只把它重建回来、却不再加载这个加载项，插件直接从功能区消失。
$log=Join-Path $env:LOCALAPPDATA 'TianGongCadSuite\panel.log'
Write-Output ('注册完成：' + $dll)
Write-Output '请重新启动天工 CAD（安装程序已确保没有旧实例占位）。'
Write-Output '换版本自检：启动 CAD 后运行下面这条，最后一行 AddInConnect 会自报实际加载的 DLL：'
Write-Output ('  Select-String -Path "'+$log+'" -Pattern AddInConnect | Select-Object -Last 1')
$cmdlog=Join-Path $env:TEMP 'cmdlog V225.txt'
Write-Output 'CAD 会话日志（更硬的证据）：启动过 CAD 之后运行下面这条，看我们的 GUID 那一段：'
Write-Output ('  Select-String -Path "'+$cmdlog+'" -Pattern "'+$guid+'" -Context 0,4 -SimpleMatch')
Write-Output '    里面 Connect: TRUE 才算 CAD 真把插件连上了；FALSE = 没加载，功能区不会有任何东西。'
