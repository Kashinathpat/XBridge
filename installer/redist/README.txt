Put the two redistributable driver installers in THIS folder before compiling
the installer (installer\XBridge.iss). They are bundled as resources and run
silently only if the corresponding service is not already present.

  ViGEmBusSetup_x64.exe
      from  https://github.com/nefarius/ViGEmBus/releases
      (Virtual Gamepad Emulation Bus driver)

  HidHideSetup_x64.exe
      from  https://github.com/nefarius/HidHide/releases
      (device-hiding filter driver)

Both are Advanced Installer EXE bootstrappers (not MSIs) - run silently with
"/exenoui /qn /norestart". Both are (c) Nefarius Software Solutions e.U. and
are licensed separately from this application. The file names must match
exactly - the .iss references them by name via ExtractTemporaryFile().
