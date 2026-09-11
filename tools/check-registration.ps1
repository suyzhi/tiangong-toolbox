$ErrorActionPreference='Stop'
try{$t=[type]::GetTypeFromProgID('TianGongPanel.AddIn',$true);$instance=[Activator]::CreateInstance($t);$instance.GetType().FullName}catch{$_ | Format-List * -Force}
