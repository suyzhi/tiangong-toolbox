param([Parameter(Mandatory=$true)][string]$PackageDirectory,[Parameter(Mandatory=$true)][string]$OutputFile)
$ErrorActionPreference='Stop'
$packagePath=[IO.Path]::GetFullPath($PackageDirectory)
$outputPath=[IO.Path]::GetFullPath($OutputFile)
if(Test-Path -LiteralPath $outputPath){throw 'Output already exists; choose a new filename.'}
[xml]$package=[IO.File]::ReadAllText((Join-Path $packagePath 'locations.xml'))
$root=$package.LineupLocationPackage
if($root.Version -ne '1'){throw 'Unsupported location package version.'}
$imagePath=Join-Path $packagePath 'view.png'
if(!(Test-Path -LiteralPath $imagePath)){throw 'Missing view.png'}
$points=@($root.Points.LineupLocationPoint)
foreach($p in $points){if($p.State -eq 'ok'){
    $x=[double]::Parse($p.X,[Globalization.CultureInfo]::InvariantCulture);$y=[double]::Parse($p.Y,[Globalization.CultureInfo]::InvariantCulture)
    if($x -lt 0 -or $x -gt 1 -or $y -lt 0 -or $y -gt 1){throw 'Coordinate out of bounds.'}
}}
$app=New-Object -ComObject ket.Application
$book=$null
try{
    $book=$app.Workbooks.Add();$sheet=$book.Worksheets.Item(1);$sheet.Name='Lineup位置图'
    $sheet.Columns.Item('A:A').ColumnWidth=3;$sheet.Columns.Item('B:I').ColumnWidth=10
    $sheet.Columns.Item('J:J').ColumnWidth=26;$sheet.Columns.Item('K:K').ColumnWidth=38;$sheet.Columns.Item('L:L').ColumnWidth=36
    $sheet.Cells.Item(1,10).Value2='编号';$sheet.Cells.Item(1,11).Value2='型号';$sheet.Cells.Item(1,12).Value2='位置状态'
    $width=[double]$sheet.Cells.Item(1,10).Left-40.0;$height=$width*[double]$root.Height/[double]$root.Width
    $picture=$sheet.Shapes.AddPicture($imagePath,0,-1,20,35,$width,$height);$picture.Name='LineupLocationImage'
    $row=3;$count=0
    foreach($p in $points){
        $cell=$sheet.Cells.Item($row,10);$cell.NumberFormat='@';$cell.Value2=[string]$p.Number
        $sheet.Cells.Item($row,11).NumberFormat='@';$sheet.Cells.Item($row,11).Value2=[string]$p.Model
        $sheet.Rows.Item($row).RowHeight=24
        if($p.State -eq 'ok'){
            $x=[double]::Parse($p.X,[Globalization.CultureInfo]::InvariantCulture);$y=[double]::Parse($p.Y,[Globalization.CultureInfo]::InvariantCulture)
            $line=$sheet.Shapes.AddLine(([double]$picture.Left+$x*[double]$picture.Width),([double]$picture.Top+$y*[double]$picture.Height),[double]$cell.Left,([double]$cell.Top+[double]$cell.Height/2))
            $line.Line.ForeColor.RGB=255;$line.Line.EndArrowheadStyle=3;$line.Line.Weight=1.25;$line.Name='LineupArrow_'+$row
            $sheet.Cells.Item($row,12).Value2='已识别，请核对';$count++
        }else{$sheet.Cells.Item($row,12).Value2='待定位：'+[string]$p.Reason}
        $row++
    }
    $book.SaveAs($outputPath,51)
    "WPS_SAVED=$outputPath";"ARROWS=$count";"POINTS=$($points.Count)";"SHAPES=$($sheet.Shapes.Count)"
}finally{if($book){$book.Close($false)}}
