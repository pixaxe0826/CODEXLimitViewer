Option Explicit

Dim shell, fileSystem, scriptDirectory, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
command = Chr(34) & scriptDirectory & "\dist\CodexQuotaOverlay.exe" & Chr(34)

shell.Run command, 0, False
