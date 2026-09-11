$ErrorActionPreference='Stop'
try{$t=[type]::GetTypeFromProgID('TianGongPanelAuto.AutoDevAddIn',$true);$instance=[Activator]::CreateInstance($t);$instance.GetType().FullName}catch{$_ | Format-List * -Force}
