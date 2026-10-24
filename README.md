# WindowPilot

**WindowPilot** is a lightweight window layout manager for Windows 11. Arrange your application windows once, save their positions, and restore your workspace whenever you need it.

- Save and restore window positions, sizes, and maximized states.
- Organize windows across multiple monitors.
- Automatically apply your saved layout when WindowPilot starts or matching windows open.
- Run quietly in the system tray, with optional Windows Auto Start.
- Check for updates through GitHub Releases.

**[Download WindowPilot](https://github.com/diabolidev/WindowPilot/releases/latest)**

## Installation

WindowPilot supports **Windows 11 (64-bit)**. You do not need Visual Studio or a separate .NET installation.

**Installer (recommended)**

1. Download `WindowPilot-Setup-win-x64.exe` from the latest [GitHub Release](https://github.com/diabolidev/WindowPilot/releases/latest).
2. Run the installer and follow the on-screen instructions.
3. Open WindowPilot from the Start menu.

**Portable version**

1. Download `WindowPilot-Portable-win-x64.zip` from the same release.
2. Extract it to a folder of your choice.
3. Run `WindowPilot.App.exe` from the extracted folder.

## How to use

1. Open the **General** tab and arrange your application windows the way you want them.
2. Click a window in the list to select it. Selected windows are highlighted; windows already saved show **IN LAYOUT**.
3. Click **Save Layout** to save the selected windows and their current positions. Saving again replaces your previous layout.
4. Click **Apply Layout** whenever you want to move matching open windows back to their saved positions.

Use the **refresh icon** to update the window list. The **trash icon** removes the saved layout and WindowPilot's local layout settings after confirmation; it does not change Windows Auto Start.

WindowPilot also attempts to restore matching windows automatically when it starts and as new windows open.

## Settings

Open the **Settings** tab to customize how WindowPilot works.

| Setting | Description |
| --- | --- |
| **Window Assignment** | **Free Slots** (default) places matching windows into available saved positions. **Individual** attempts to match each window to its own saved position. |
| **Auto Start** | Starts WindowPilot in the background when you sign in to Windows. |
| **Check for Updates** | Manually checks GitHub for a newer release. WindowPilot also checks once when it starts. |
| **Show EXE** | Opens File Explorer and selects the WindowPilot executable. |

### Running in the background

- **Minimize** WindowPilot to keep it running in the system tray, out of the taskbar.
- **Click the tray icon** to bring the window back.
- **Close** the main window to exit WindowPilot completely.

## Updates

When a newer version is available, WindowPilot notifies you. You can also use **Settings → Check for Updates** at any time. Downloading and installing an update requires your confirmation.

The built-in updater uses the installer release. If you prefer to keep using the portable edition, download and extract the newer portable ZIP manually after closing WindowPilot.

## Good to know

WindowPilot stores layouts locally and does not require an account. Window management depends on the information applications expose to Windows, so some programs or protected windows may not support automatic placement. If you change your monitor setup, arrange your windows again and save a new layout.

---

Developed by **[diabolidev](https://github.com/diabolidev)**.
