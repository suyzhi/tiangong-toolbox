param([switch]$CloseOwnedPickTests)
$ErrorActionPreference='Stop'
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Runtime.InteropServices.ComTypes;
public static class RotCad {
 [DllImport("ole32.dll")] public static extern int GetRunningObjectTable(int reserved,out IRunningObjectTable rot);
 [DllImport("ole32.dll")] public static extern int CreateBindCtx(int reserved,out IBindCtx ctx);
 public sealed class Entry { public string Name;public object Document; }
 public static Entry[] Documents(string root){IRunningObjectTable rot;IBindCtx ctx;GetRunningObjectTable(0,out rot);CreateBindCtx(0,out ctx);IEnumMoniker en;rot.EnumRunning(out en);var one=new IMoniker[1];var list=new System.Collections.Generic.List<Entry>();while(en.Next(1,one,IntPtr.Zero)==0){string name;one[0].GetDisplayName(ctx,null,out name);if(name.StartsWith(root,StringComparison.OrdinalIgnoreCase) && name.EndsWith(".asm",StringComparison.OrdinalIgnoreCase)){object value;rot.GetObject(one[0],out value);list.Add(new Entry{Name=name,Document=value});}}return list.ToArray();}
}
'@
$ownedRoot=[IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))+'\'
foreach($entry in [RotCad]::Documents($ownedRoot)){
 $name=$entry.Name
 if($name -notmatch '(?i)\.asm$'){continue}
 Write-Output $name
 if(!$CloseOwnedPickTests -or $name -notmatch 'pick-20260908-(105754|105859)\\Frame\.asm$'){continue}
 $doc=$entry.Document;$app=$doc.Application
 $paths=@($app.Documents | ForEach-Object FullName)
 if($paths.Count -ne 1 -or !$paths[0].StartsWith($ownedRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected documents; refusing cleanup.'}
 $doc.Close($false);$app.Quit();Write-Output 'Closed owned pick test only.'
}
