param([switch]$Uninstall,[string]$LibraryPath)
$ErrorActionPreference='Stop'
if(![Environment]::Is64BitProcess){throw 'Please run 64-bit Windows PowerShell.'}
$root=Split-Path $PSScriptRoot -Parent
$guid='{B3B28C54-11D0-4488-B226-AD62429CEED2}'
$prog='TianGongPanelAuto.AutoDevAddIn'
$classPath='Software\Classes\CLSID\'+$guid
$reg=[Microsoft.Win32.Registry]::CurrentUser
if($Uninstall){
    $reg.DeleteSubKeyTree($classPath,$false)
    $reg.DeleteSubKeyTree('Software\Classes\'+$prog,$false)
    Write-Output 'Unregistered TianGongPanelAuto for the current user. Restart CAD to unload it.'
    exit
}
$dll=if($LibraryPath){[IO.Path]::GetFullPath($LibraryPath)}else{Join-Path $root 'build\TianGongPanelAuto.dll'}
if(!(Test-Path $dll)){throw 'Run tools\build.ps1 first.'}
$assembly=[Reflection.AssemblyName]::GetAssemblyName($dll)
$key=$reg.CreateSubKey($classPath)
$key.SetValue('','TianGongPanelAuto.AutoDevAddIn')
$key.SetValue('409','Rectangle Panel (DEV 0.3.0)')
$key.SetValue('804',[string]([char]0x77E9)+[char]0x5F62+[char]0x677F+' (DEV 0.3.0)')
$key.SetValue('AutoConnect',1,[Microsoft.Win32.RegistryValueKind]::DWord)
$key.CreateSubKey('Implemented Categories\{26B1D2D1-2B03-11D2-B589-080036E8B802}').Dispose()
$key.CreateSubKey('Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}').Dispose()
$key.CreateSubKey('Environment Categories\{26618395-09D6-11D1-BA07-080036230602}').Dispose()
$server=$key.CreateSubKey('InprocServer32')
$server.SetValue('','mscoree.dll');$server.SetValue('ThreadingModel','Both')
foreach($target in @($server,$server.CreateSubKey($assembly.Version.ToString()))){
    $target.SetValue('Assembly',$assembly.FullName);$target.SetValue('Class','TianGongPanelAuto.PanelAddIn')
    $target.SetValue('RuntimeVersion','v4.0.30319');$target.SetValue('CodeBase','file:///'+$dll.Replace('\','/'))
}
$key.CreateSubKey('ProgId').SetValue('',$prog)
$reg.CreateSubKey('Software\Classes\'+$prog+'\CLSID').SetValue('',$guid)
$key.Dispose();$server.Dispose()
Write-Output ('Registered for current user: '+$dll)
Write-Output 'Use the TianGong CAD add-in manager to enable Rectangle Panel (DEV 0.3.0), or restart CAD.'
