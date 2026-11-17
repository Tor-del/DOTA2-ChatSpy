# AGENTS.md

- DOTA2 Chat Spy is a WPF application for Windows. Use the .NET 10 SDK.
- Run `dotnet build DOTA2ChatSpy.csproj -c Release` to verify changes. For UI changes, run the app and check both EN and RU.
- Keep interface and export text in sync across `UiStrings.resx` and `UiStrings.ru.resx`. English is the default. Preserve the original chat text from OpenDota.
- Keep startup free of prefilled account IDs and network requests. Previously entered IDs are available through ID history.
- Keep build outputs, installers, local cache data, and real player IDs out of Git. Use fictional data for screenshots or fixtures.
- Follow the build and release commands in `README.md`.
