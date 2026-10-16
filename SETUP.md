# Setting Up DWG Tools on a New Computer

About 15 minutes. You need a Windows PC with AutoCAD Plant 3D (or AutoCAD) **2020 or newer**.

> **AutoCAD / Plant 3D 2025 or newer** runs on modern .NET, so that computer also needs the free **.NET SDK**
> of the same version (step 1): **.NET 10** for an updated AutoCAD 2025, **.NET 8** for 2025 as first released.
> Not sure? Run `setup.bat`; if the SDK is missing it says exactly which one to install. 2020-2024 need nothing extra.

## 1. Install the Prerequisites (once)

| What | Where | Notes |
|---|---|---|
| **Python 3.10 or newer** | https://www.python.org/downloads/ | On the first installer screen tick **Add python.exe to PATH**. |
| **Git for Windows** | https://git-scm.com/download/win | Defaults are fine. |
| **AutoCAD Plant 3D 2020 or newer** | Autodesk | Already installed on CAD machines. |
| **.NET SDK** (2025+ only) | `winget install Microsoft.DotNet.SDK.10` (or `.8`), or https://dotnet.microsoft.com/download (SDK, Windows x64) | Only for AutoCAD 2025 and newer; the version setup.bat asks for. |
| **GitHub access** | Ask for an invite to the repo | Accept the email invite before cloning. |

Nothing else is needed: the AutoCAD plugin is compiled on each computer against that computer's AutoCAD
(2020-2024 with the C# compiler that comes with Windows; 2025+ with the .NET SDK). No Visual Studio.

## 2. Get the Code

Open **Command Prompt** and clone into a folder **without spaces** in the path:

```bat
mkdir C:\Tools
cd C:\Tools
git clone https://github.com/energydatachpguy/dwg-tools.git
cd dwg-tools
```

The first time, Git asks you to sign in to GitHub in the browser. Do that once and it remembers.

## 3. Run Setup

Double-click **`setup.bat`** in the `dwg-tools` folder. It:

1. Finds Python.
2. Creates the Python environment (`venv`) and installs the packages.
3. Creates **`settings.json`** for this computer (never shared through Git).
4. Builds the AutoCAD plugin against **this computer's** AutoCAD and says which version it found.
5. Runs the safety audit and the setup check.

The last screen lists every item as **OK**, **WARN** or **FAIL**, with what to do for anything that isn't OK.
Run the check again any time with:

```bat
venv\Scripts\python.exe tools\check_setup.py
```

## 4. Start It

1. Start **Plant 3D** and open a **copy** of the drawing you are working on.
2. Double-click **`run.bat`**. The page opens at http://localhost:5014. Keep the black window open while you work.
3. The first time the app talks to Plant 3D, AutoCAD asks whether to load the unsigned plugin
   (*DwgTools_....dll*). The app clicks **Load Once** for you; if it doesn't, click it yourself.

`run.bat` is for this computer only. **`run_shared.bat`** lets other computers on the network open the page
(the address is printed in the window). Or put `"share": true` in `settings.json` to always share.

## 5. Get Updates

```bat
cd C:\Tools\dwg-tools
git pull
```

Then restart `run.bat`. It rebuilds the plugin automatically when the plugin source changed, and Plant 3D does not need
a restart (every build gets its own name and command). If `requirements.txt` changed, run `setup.bat` again.

## settings.json (This Computer Only)

| Key | Default | Use it when |
|---|---|---|
| `port` | `5014` | Something else uses port 5014. |
| `share` | `false` | `true` = other computers on the network can open the page. |
| `acad_exe` | `""` (find it) | Several AutoCAD versions are installed, or AutoCAD is not under `C:\Program Files\Autodesk`. Full path, e.g. `"C:\\Program Files\\Autodesk\\AutoCAD 2022\\acad.exe"` (double backslashes). |
| `acad_args` | Plant 3D | Plain AutoCAD (no Plant 3D): set to `[]`. |

After changing `acad_exe`, run `setup.bat` again so the plugin is rebuilt against that version.

## If Something Goes Wrong

| Problem | Fix |
|---|---|
| `'python' is not recognized` | Reinstall Python with **Add python.exe to PATH** ticked, then open a new Command Prompt. |
| `THE AUTOCAD PLUGIN DID NOT BUILD` | Read `plugin\build.log`. Usually AutoCAD wasn't found (set `acad_exe`), or it is 2025+ and the .NET SDK it names isn't installed (install it, open a new Command Prompt, run setup.bat again). |
| `settings.json can't be read (Invalid \escape)` | Backslashes in JSON must be doubled: `"C:\\Program Files\\..."`. Or leave `acad_exe` empty. |
| Page says AutoCAD isn't running | Start Plant 3D first. If Plant 3D runs **as administrator**, run `run.bat` as administrator too (they must match). |
| `Port 5014 is in use` | DWG Tools is already open in another window, or set another `port`. |
| `git clone` says *Repository not found* | Accept the GitHub invite first, and sign in as the invited account. |

## Safety, Same on Every Computer

- Work on a **copy** of the master drawing.
- The tool **never saves a drawing**; inserting changes the open drawing in memory only. You save.
- The tool never deletes or overwrites a file it didn't make; `run.bat` refuses to start if the safety audit fails.
