Option Explicit

Dim fso
Set fso = CreateObject("Scripting.FileSystemObject")

Dim scriptDir, musicRoot, outputPath, onPlayList, minSunlight
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)

musicRoot = scriptDir
outputPath = ""
onPlayList = "survival|creative"
minSunlight = 5

Dim trackNameSuffix
trackNameSuffix = " (Tottomori Music)"

If WScript.Arguments.Count >= 1 Then
	If Trim(WScript.Arguments(0)) <> "" Then musicRoot = WScript.Arguments(0)
End If

musicRoot = fso.GetAbsolutePathName(musicRoot)

If WScript.Arguments.Count >= 2 Then
	outputPath = WScript.Arguments(1)
End If

If Trim(outputPath) = "" Then
	outputPath = fso.BuildPath(musicRoot, "musicconfig.json")
Else
	outputPath = fso.GetAbsolutePathName(outputPath)
End If

If WScript.Arguments.Count >= 3 Then
	If Trim(WScript.Arguments(2)) <> "" Then onPlayList = WScript.Arguments(2)
End If

If WScript.Arguments.Count >= 4 Then
	If Trim(WScript.Arguments(3)) <> "" Then minSunlight = CLng(WScript.Arguments(3))
End If

Dim tracks(), trackCount
ReDim tracks(0)
trackCount = 0

Dim unsupported(), unsupportedCount
ReDim unsupported(0)
unsupportedCount = 0

Dim dayFiles(), dayCount
ReDim dayFiles(0)
dayCount = 0

Dim nightFiles(), nightCount
ReDim nightFiles(0)
nightCount = 0

CollectOggFiles musicRoot, "day", dayFiles, dayCount
CollectOggFiles musicRoot, "night", nightFiles, nightCount

Dim i, rel, trackName

For i = 0 To dayCount - 1
	rel = GetRelativeMusicPathWithoutExtension(musicRoot, dayFiles(i))
	trackName = GetTrackName(rel)

	AddTrack NewSurfaceTrackBlock( _
		rel, _
		trackName & " [Day]", _
		Array( _
			"minhour: 6,", _
			"maxhour: 18,", _
			"minSunlight: " & CStr(minSunlight) & "," _
		) _
	)
Next

For i = 0 To nightCount - 1
	rel = GetRelativeMusicPathWithoutExtension(musicRoot, nightFiles(i))
	trackName = GetTrackName(rel)

	AddTrack NewSurfaceTrackBlock( _
		rel, _
		trackName & " [Night Late]", _
		Array( _
			"minhour: 18,", _
			"maxhour: 24,", _
			"minSunlight: " & CStr(minSunlight) & "," _
		) _
	)

	AddTrack NewSurfaceTrackBlock( _
		rel, _
		trackName & " [Night Early]", _
		Array( _
			"minhour: 0,", _
			"maxhour: 6,", _
			"minSunlight: " & CStr(minSunlight) & "," _
		) _
	)
Next

CollectUnsupportedFiles musicRoot, "day", unsupported, unsupportedCount
CollectUnsupportedFiles musicRoot, "night", unsupported, unsupportedCount

Dim configText
configText = "{" & vbCrLf
configText = configText & vbTab & "tracks: [" & vbCrLf

For i = 0 To trackCount - 1
	configText = configText & tracks(i)
	If i < trackCount - 1 Then
		configText = configText & ","
	End If
	configText = configText & vbCrLf
Next

configText = configText & vbTab & "]," & vbCrLf
configText = configText & "}" & vbCrLf

WriteUtf8File outputPath, configText

Dim message
message = "Generated musicconfig.json" & vbCrLf & _
	"Output: " & outputPath & vbCrLf & _
	"Day tracks: " & CStr(dayCount) & vbCrLf & _
	"Night tracks: " & CStr(nightCount) & " files, " & CStr(nightCount * 2) & " entries" & vbCrLf & _
	"Total generated entries: " & CStr(trackCount)

If unsupportedCount > 0 Then
	message = message & vbCrLf & vbCrLf & _
		"WARNING: Found files that are not .ogg:" & vbCrLf

	Dim maxWarnings
	maxWarnings = unsupportedCount
	If maxWarnings > 20 Then maxWarnings = 20

	For i = 0 To maxWarnings - 1
		message = message & "Wrong format file: " & unsupported(i) & vbCrLf
	Next

	If unsupportedCount > maxWarnings Then
		message = message & "...and " & CStr(unsupportedCount - maxWarnings) & " more." & vbCrLf
	End If
End If

WScript.Echo message

Sub AddTrack(ByVal blockText)
	If trackCount > UBound(tracks) Then ReDim Preserve tracks(trackCount)
	tracks(trackCount) = blockText
	trackCount = trackCount + 1
End Sub

Sub CollectOggFiles(ByVal root, ByVal folderName, ByRef files, ByRef count)
	Dim folderPath
	folderPath = fso.BuildPath(root, folderName)

	count = 0
	ReDim files(0)

	If Not fso.FolderExists(folderPath) Then Exit Sub

	CollectOggFilesRecursive folderPath, files, count
	SortStringArray files, count
End Sub

Sub CollectOggFilesRecursive(ByVal folderPath, ByRef files, ByRef count)
	Dim folderObj, fileObj, subfolderObj
	Set folderObj = fso.GetFolder(folderPath)

	For Each fileObj In folderObj.Files
		If LCase(fso.GetExtensionName(fileObj.Name)) = "ogg" Then
			If count > UBound(files) Then ReDim Preserve files(count)
			files(count) = fileObj.Path
			count = count + 1
		End If
	Next

	For Each subfolderObj In folderObj.SubFolders
		CollectOggFilesRecursive subfolderObj.Path, files, count
	Next
End Sub

Sub CollectUnsupportedFiles(ByVal root, ByVal folderName, ByRef files, ByRef count)
	Dim folderPath
	folderPath = fso.BuildPath(root, folderName)

	If Not fso.FolderExists(folderPath) Then Exit Sub

	CollectUnsupportedFilesRecursive folderPath, files, count
	SortStringArray files, count
End Sub

Sub CollectUnsupportedFilesRecursive(ByVal folderPath, ByRef files, ByRef count)
	Dim folderObj, fileObj, subfolderObj
	Set folderObj = fso.GetFolder(folderPath)

	For Each fileObj In folderObj.Files
		If LCase(fso.GetExtensionName(fileObj.Name)) <> "ogg" Then
			If count > UBound(files) Then ReDim Preserve files(count)
			files(count) = fileObj.Path
			count = count + 1
		End If
	Next

	For Each subfolderObj In folderObj.SubFolders
		CollectUnsupportedFilesRecursive subfolderObj.Path, files, count
	Next
End Sub

Sub SortStringArray(ByRef arr, ByVal count)
	Dim a, b, temp

	If count < 2 Then Exit Sub

	For a = 0 To count - 2
		For b = a + 1 To count - 1
			If LCase(arr(a)) > LCase(arr(b)) Then
				temp = arr(a)
				arr(a) = arr(b)
				arr(b) = temp
			End If
		Next
	Next
End Sub

Function GetRelativeMusicPathWithoutExtension(ByVal root, ByVal filePath)
	Dim rootFull, rootPrefix, fileFull, rel, lastDot

	rootFull = TrimTrailingSlashes(fso.GetAbsolutePathName(root))
	rootPrefix = rootFull & "\"
	fileFull = fso.GetAbsolutePathName(filePath)

	If LCase(Left(fileFull, Len(rootPrefix))) <> LCase(rootPrefix) Then
		Err.Raise vbObjectError + 1000, "configgen.vbs", "File is not under music root: " & fileFull
	End If

	rel = Mid(fileFull, Len(rootPrefix) + 1)

	lastDot = InStrRev(rel, ".")
	If lastDot > 0 Then rel = Left(rel, lastDot - 1)

	GetRelativeMusicPathWithoutExtension = Replace(rel, "\", "/")
End Function

Function TrimTrailingSlashes(ByVal text)
	Do While Len(text) > 0 And (Right(text, 1) = "\" Or Right(text, 1) = "/")
		text = Left(text, Len(text) - 1)
	Loop

	TrimTrailingSlashes = text
End Function

Function GetTrackName(ByVal relativePathWithoutExtension)
	Dim name, slashPos

	name = relativePathWithoutExtension
	slashPos = InStrRev(name, "/")
	If slashPos > 0 Then name = Mid(name, slashPos + 1)

	GetTrackName = name & trackNameSuffix
End Function

Function CollapseSpaces(ByVal text)
	Do While InStr(text, "  ") > 0
		text = Replace(text, "  ", " ")
	Loop

	CollapseSpaces = Trim(text)
End Function

Function ToTitleCase(ByVal text)
	Dim parts, i, part, result

	text = LCase(text)
	parts = Split(text, " ")
	result = ""

	For i = 0 To UBound(parts)
		part = parts(i)
		If Len(part) > 0 Then
			part = UCase(Left(part, 1)) & Mid(part, 2)
			If result <> "" Then result = result & " "
			result = result & part
		End If
	Next

	ToTitleCase = result
End Function

Function NewSurfaceTrackBlock(ByVal filePath, ByVal trackName, ByVal ruleLines)
	Dim text, i

	text = vbTab & "{" & vbCrLf
	text = text & vbTab & vbTab & """$type"": ""Vintagestory.API.Client.SurfaceMusicTrack, VintagestoryAPI""," & vbCrLf
	text = text & vbTab & vbTab & "file: " & QuoteJson5String(filePath) & "," & vbCrLf
	text = text & vbTab & vbTab & "name: " & QuoteJson5String(trackName) & "," & vbCrLf
	text = text & vbTab & vbTab & "onPlayList: " & QuoteJson5String(onPlayList) & "," & vbCrLf

	For i = 0 To UBound(ruleLines)
		text = text & vbTab & vbTab & ruleLines(i) & vbCrLf
	Next

	text = text & vbTab & "}"

	NewSurfaceTrackBlock = text
End Function

Function QuoteJson5String(ByVal text)
	QuoteJson5String = """" & EscapeJsonString(text) & """"
End Function

Function EscapeJsonString(ByVal text)
	Dim result, i, ch, code

	result = ""

	For i = 1 To Len(text)
		ch = Mid(text, i, 1)
		code = AscW(ch)
		If code < 0 Then code = code + 65536

		Select Case ch
			Case "\"
				result = result & "\\"
			Case """"
				result = result & "\"""
			Case vbTab
				result = result & "\t"
			Case vbCr
				result = result & "\r"
			Case vbLf
				result = result & "\n"
			Case Else
				If code < 32 Then
					result = result & "\u" & Right("0000" & Hex(code), 4)
				Else
					result = result & ch
				End If
		End Select
	Next

	EscapeJsonString = result
End Function

Sub WriteUtf8File(ByVal path, ByVal text)
	Dim stream
	Set stream = CreateObject("ADODB.Stream")

	stream.Type = 2
	stream.Charset = "utf-8"
	stream.Open
	stream.WriteText text
	stream.SaveToFile path, 2
	stream.Close
End Sub
