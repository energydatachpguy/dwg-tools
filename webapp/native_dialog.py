"""
Windows' own Open / Save As dialogs, shown on the desktop of the machine the
app runs on. Only returns the path the user picked; it never reads, writes,
moves or deletes anything itself.
"""
import os
import subprocess

_PS = r"""
Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.Application]::EnableVisualStyles()
$owner = New-Object System.Windows.Forms.Form
$owner.TopMost = $true; $owner.ShowInTaskbar = $false; $owner.WindowState = 'Minimized'
$owner.Show(); $owner.Hide()
if ($env:DLG_KIND -eq 'save') { $d = New-Object System.Windows.Forms.SaveFileDialog; $d.OverwritePrompt = $false; $d.AddExtension = $true; $d.DefaultExt = 'dwg' }
else { $d = New-Object System.Windows.Forms.OpenFileDialog; $d.CheckFileExists = $true; if ($env:DLG_KIND -eq 'multi') { $d.Multiselect = $true } }
$d.Title = $env:DLG_TITLE
$d.Filter = 'AutoCAD drawings (*.dwg)|*.dwg'
if ($env:DLG_DIR -and (Test-Path -LiteralPath $env:DLG_DIR)) { $d.InitialDirectory = $env:DLG_DIR }
if ($env:DLG_NAME) { $d.FileName = $env:DLG_NAME }
$r = $d.ShowDialog($owner)
$owner.Dispose()
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
if ($r -eq 'OK') { if ($env:DLG_KIND -eq 'multi') { [Console]::Out.Write(($d.FileNames -join [char]10)) } else { [Console]::Out.Write($d.FileName) } }
"""


def ask(kind, title, initial_dir="", name=""):
    """kind: 'open', 'multi' (several files, one per line) or 'save'. -> chosen path(s), or '' if cancelled. Raises on failure."""
    if os.name != "nt":
        raise RuntimeError("native dialogs are only available on Windows")
    env = dict(os.environ, DLG_KIND=kind, DLG_TITLE=title, DLG_DIR=initial_dir or "", DLG_NAME=name or "")
    p = subprocess.run(["powershell", "-NoProfile", "-STA", "-ExecutionPolicy", "Bypass", "-Command", _PS],
                       env=env, capture_output=True, timeout=1800,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if p.returncode != 0:
        raise RuntimeError((p.stderr or b"").decode("utf-8", "replace").strip()[:400] or "dialog failed")
    return p.stdout.decode("utf-8", "replace").strip()
