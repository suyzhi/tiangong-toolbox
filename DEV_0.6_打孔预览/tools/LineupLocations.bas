Attribute VB_Name = "LineupLocations"
Option Explicit

' Import this module in WPS VBA. Always creates a new worksheet.
Public Sub ImportLineupLocationPackage()
    On Error GoTo Failed
    Dim path As Variant, dom As Object, root As Object, points As Object, p As Object
    Dim ws As Object, pic As Object, cell As Object, arrow As Object
    Dim folder As String, imagePath As String, row As Long, x As Double, y As Double
    Dim width As Double, height As Double
    path = Application.GetOpenFilename("Lineup XML (*.xml),*.xml", , "Select locations.xml")
    If VarType(path) = vbBoolean Then Exit Sub
    Set dom = CreateObject("MSXML2.DOMDocument.6.0")
    dom.async = False
    dom.resolveExternals = False
    If Not dom.Load(CStr(path)) Then Err.Raise 5, , "Invalid XML"
    Set root = dom.SelectSingleNode("/LineupLocationPackage")
    If root Is Nothing Then Err.Raise 5, , "Not a Lineup location package"
    If root.SelectSingleNode("Version").Text <> "1" Then Err.Raise 5, , "Unsupported version"
    folder = Left(CStr(path), InStrRev(CStr(path), "\"))
    imagePath = folder & "view.png"
    If Dir(imagePath) = "" Then Err.Raise 5, , "Missing view.png"
    Set points = root.SelectNodes("Points/LineupLocationPoint")
    For Each p In points
        If p.SelectSingleNode("State").Text = "ok" Then
            x = Val(p.SelectSingleNode("X").Text): y = Val(p.SelectSingleNode("Y").Text)
            If x < 0 Or x > 1 Or y < 0 Or y > 1 Then Err.Raise 5, , "Coordinates outside image"
        End If
    Next
    width = 560: height = width * Val(root.SelectSingleNode("Height").Text) / Val(root.SelectSingleNode("Width").Text)
    Set ws = ActiveWorkbook.Worksheets.Add
    ws.Columns("A:A").ColumnWidth = 3
    ws.Columns("B:I").ColumnWidth = 10
    ws.Columns("J:J").ColumnWidth = 26
    ws.Columns("K:K").ColumnWidth = 38
    ws.Columns("L:L").ColumnWidth = 36
    ws.Cells(1, 10).Value2 = "Number": ws.Cells(1, 11).Value2 = "Model": ws.Cells(1, 12).Value2 = "Location status"
    width = ws.Cells(1, 10).Left - 40
    height = width * Val(root.SelectSingleNode("Height").Text) / Val(root.SelectSingleNode("Width").Text)
    Set pic = ws.Shapes.AddPicture(imagePath, 0, -1, 20, 35, width, height)
    pic.Name = "LineupLocationImage"
    row = 3
    For Each p In points
        Set cell = ws.Cells(row, 10)
        cell.NumberFormat = "@": cell.Value2 = p.SelectSingleNode("Number").Text
        ws.Cells(row, 11).NumberFormat = "@": ws.Cells(row, 11).Value2 = p.SelectSingleNode("Model").Text
        ws.Rows(row).RowHeight = 24
        If p.SelectSingleNode("State").Text = "ok" Then
            x = Val(p.SelectSingleNode("X").Text): y = Val(p.SelectSingleNode("Y").Text)
            Set arrow = ws.Shapes.AddLine(pic.Left + x * pic.Width, pic.Top + y * pic.Height, cell.Left, cell.Top + cell.Height / 2)
            arrow.Line.ForeColor.RGB = RGB(255, 0, 0)
            arrow.Line.EndArrowheadStyle = 3: arrow.Line.Weight = 1.25
            arrow.Name = "LineupArrow_" & CStr(row)
            ws.Cells(row, 12).Value2 = "Detected - review point"
        Else
            ws.Cells(row, 12).Value2 = p.SelectSingleNode("Reason").Text
        End If
        row = row + 1
    Next
    MsgBox "Location sheet created. Review arrows before use."
    Exit Sub
Failed:
    MsgBox Err.Description, vbExclamation, "Lineup"
End Sub

' Select the un-cropped package image first, then run this macro.
' Arrow geometry is recalculated from the current image size on each run.
Public Sub LinkSelectedPictureToCell()
    On Error GoTo Failed
    Dim pic As Object, target As Object, ws As Object, dom As Object, root As Object
    Dim p As Object, found As Object, arrow As Object, path As Variant, number As String
    Dim x As Double, y As Double, startX As Double, expectedRatio As Double
    Set pic = Selection.ShapeRange.Item(1)
    Set ws = ActiveSheet
    If pic.Type <> 13 Then Err.Raise 5, , "Select a picture first"
    If pic.Rotation <> 0 Then Err.Raise 5, , "Use an unrotated picture"
    If pic.PictureFormat.CropLeft <> 0 Or pic.PictureFormat.CropTop <> 0 Or pic.PictureFormat.CropRight <> 0 Or pic.PictureFormat.CropBottom <> 0 Then Err.Raise 5, , "Use the complete, uncropped view.png"
    path = Application.GetOpenFilename("Lineup XML (*.xml),*.xml", , "Select locations.xml for this picture")
    If VarType(path) = vbBoolean Then Exit Sub
    Set dom = CreateObject("MSXML2.DOMDocument.6.0")
    dom.async = False: dom.resolveExternals = False
    If Not dom.Load(CStr(path)) Then Err.Raise 5, , "Invalid XML"
    Set root = dom.SelectSingleNode("/LineupLocationPackage")
    If root Is Nothing Then Err.Raise 5, , "Not a Lineup package"
    If root.SelectSingleNode("Version").Text <> "1" Then Err.Raise 5, , "Unsupported version"
    number = InputBox("Lineup number to label")
    If number = "" Then Exit Sub
    For Each p In root.SelectNodes("Points/LineupLocationPoint")
        If StrComp(p.SelectSingleNode("Number").Text, number, vbTextCompare) = 0 Then
            If Not found Is Nothing Then Err.Raise 5, , "Duplicate number in package"
            Set found = p
        End If
    Next
    If found Is Nothing Then Err.Raise 5, , "Number not found"
    If found.SelectSingleNode("State").Text <> "ok" Then Err.Raise 5, , "This point needs a new CAD view"
    x = Val(found.SelectSingleNode("X").Text): y = Val(found.SelectSingleNode("Y").Text)
    If x < 0 Or x > 1 Or y < 0 Or y > 1 Then Err.Raise 5, , "Invalid coordinates"
    Set target = Application.InputBox("Select an EMPTY number cell; model goes in the next column", "Lineup", Type:=8)
    Set target = target.Cells(1, 1)
    If Not target.Parent Is ws Then Err.Raise 5, , "Select a cell on the picture sheet"
    If target.HasFormula Or target.Offset(0, 1).HasFormula Or Len(CStr(target.Value2)) > 0 Or Len(CStr(target.Offset(0, 1).Value2)) > 0 Then Err.Raise 5, , "Both label cells must be empty"
    startX = target.Left
    If target.Left < pic.Left Then startX = target.Left + target.Width
    Set arrow = ws.Shapes.AddLine(pic.Left + x * pic.Width, pic.Top + y * pic.Height, startX, target.Top + target.Height / 2)
    arrow.Line.ForeColor.RGB = RGB(255, 0, 0): arrow.Line.EndArrowheadStyle = 3: arrow.Line.Weight = 1.25
    target.NumberFormat = "@": target.Value2 = found.SelectSingleNode("Number").Text
    target.Offset(0, 1).NumberFormat = "@": target.Offset(0, 1).Value2 = found.SelectSingleNode("Model").Text
    Exit Sub
Failed:
    MsgBox Err.Description, vbExclamation, "Lineup"
End Sub
