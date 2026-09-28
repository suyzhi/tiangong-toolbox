$ErrorActionPreference='Stop'
try{$t=[type]::GetTypeFromProgID('TianGongCadSuite.SuiteDevAddIn',$true);$instance=[Activator]::CreateInstance($t);$instance.GetType().FullName}catch{$_ | Format-List * -Force}
